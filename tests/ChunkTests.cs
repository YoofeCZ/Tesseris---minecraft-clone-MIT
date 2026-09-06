using Tesseris.Game.Blocks;
using Tesseris.Game.World;
using Xunit;

namespace Tesseris.Tests;

/// <summary>
/// Testy paletového kódování chunku. Hlídají hlavně přechody mezi bitovými šířkami —
/// tam se při přebalování dá snadno ztratit nebo posunout obsah.
/// </summary>
public sealed class ChunkTests
{
    [Fact]
    public void Novy_chunk_je_homogenni_a_nema_pole_indexu()
    {
        var chunk = new Chunk();

        Assert.True(chunk.IsHomogeneous);
        Assert.Equal(BlockRegistry.Air, chunk.HomogeneousBlock);
        Assert.Equal(0, chunk.BitsPerIndex);
        Assert.Equal(1, chunk.PaletteCount);
    }

    [Fact]
    public void Zapis_stejneho_bloku_homogenni_chunk_nerozbali()
    {
        var chunk = new Chunk(fill: 7);

        chunk.SetBlock(5, 6, 7, 7);

        Assert.True(chunk.IsHomogeneous);
        Assert.Equal(0, chunk.BitsPerIndex);
    }

    [Fact]
    public void Prvni_jiny_blok_prepne_na_jeden_bit()
    {
        var chunk = new Chunk(fill: 1);

        chunk.SetBlock(0, 0, 0, 2);

        Assert.False(chunk.IsHomogeneous);
        Assert.Equal(1, chunk.BitsPerIndex);
        Assert.Equal(2, chunk.PaletteCount);
        Assert.Equal(2, chunk.GetBlock(0, 0, 0));
        Assert.Equal(1, chunk.GetBlock(1, 0, 0));
        Assert.Equal(1, chunk.GetBlock(31, 31, 31));
    }

    [Theory]
    [InlineData(2, 1)]
    [InlineData(3, 2)]
    [InlineData(4, 2)]
    [InlineData(5, 4)]
    [InlineData(16, 4)]
    [InlineData(17, 8)]
    [InlineData(200, 8)]
    public void Sirka_indexu_roste_podle_velikosti_palety(int distinctBlocks, int expectedBits)
    {
        var chunk = new Chunk(fill: 0);

        for (int i = 1; i < distinctBlocks; i++)
        {
            chunk.SetBlock(i, 0, 0, (ushort)i);
        }

        Assert.Equal(expectedBits, chunk.BitsPerIndex);
        Assert.Equal(distinctBlocks, chunk.PaletteCount);
    }

    [Fact]
    public void Prebaleni_na_sirsi_index_zachova_vsechen_obsah()
    {
        var chunk = new Chunk(fill: 0);

        // Zapíše se vzorek, který přežije všechny tři přechody 1 → 2 → 4 → 8 bitů.
        for (int i = 0; i < 300; i++)
        {
            int x = i % Chunk.Size;
            int y = (i / Chunk.Size) % Chunk.Size;
            int z = i / (Chunk.Size * Chunk.Size);
            chunk.SetBlock(x, y, z, (ushort)(1 + (i % 200)));
        }

        Assert.Equal(8, chunk.BitsPerIndex);

        for (int i = 0; i < 300; i++)
        {
            int x = i % Chunk.Size;
            int y = (i / Chunk.Size) % Chunk.Size;
            int z = i / (Chunk.Size * Chunk.Size);
            Assert.Equal((ushort)(1 + (i % 200)), chunk.GetBlock(x, y, z));
        }
    }

    [Fact]
    public void Prepis_bloku_neovlivni_sousedy()
    {
        var chunk = new Chunk(fill: 0);

        for (int i = 0; i < 8; i++)
        {
            chunk.SetBlock(i, 0, 0, (ushort)(i + 1));
        }

        chunk.SetBlock(3, 0, 0, 99);

        Assert.Equal(99, chunk.GetBlock(3, 0, 0));
        Assert.Equal(3, chunk.GetBlock(2, 0, 0));
        Assert.Equal(5, chunk.GetBlock(4, 0, 0));
    }

    [Fact]
    public void Prekroceni_meze_palety_skonci_srozumitelnou_vyjimkou()
    {
        var chunk = new Chunk(fill: 0);

        // 255 dalších typů zaplní osmibitovou paletu i s výchozím blokem.
        for (int i = 1; i < Chunk.MaxPaletteEntries; i++)
        {
            chunk.SetBlock(i % Chunk.Size, i / Chunk.Size, 0, (ushort)i);
        }

        Assert.Equal(Chunk.MaxPaletteEntries, chunk.PaletteCount);

        var exception = Assert.Throws<InvalidOperationException>(
            () => chunk.SetBlock(31, 31, 31, 60000));

        Assert.Contains("256", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Fill_vrati_chunk_zpet_do_homogenniho_stavu()
    {
        var chunk = new Chunk(fill: 0);
        chunk.SetBlock(1, 2, 3, 42);
        Assert.False(chunk.IsHomogeneous);

        chunk.Fill(5);

        Assert.True(chunk.IsHomogeneous);
        Assert.Equal(5, chunk.GetBlock(1, 2, 3));
        Assert.Equal(5, chunk.GetBlock(31, 31, 31));
    }

    [Fact]
    public void CopyTo_rozbali_homogenni_i_paletovy_chunk()
    {
        var homogeneous = new Chunk(fill: 3);
        ushort[] buffer = new ushort[Chunk.Volume];

        homogeneous.CopyTo(buffer);
        Assert.All(buffer, value => Assert.Equal(3, value));

        var mixed = new Chunk(fill: 3);
        mixed.SetBlock(2, 0, 0, 9);
        mixed.CopyTo(buffer);

        Assert.Equal(9, buffer[Chunk.LocalIndex(2, 0, 0)]);
        Assert.Equal(3, buffer[Chunk.LocalIndex(3, 0, 0)]);
    }

    [Fact]
    public void LocalIndex_je_prosty_na_celem_objemu()
    {
        var seen = new HashSet<int>();

        for (int y = 0; y < Chunk.Size; y++)
        {
            for (int z = 0; z < Chunk.Size; z++)
            {
                for (int x = 0; x < Chunk.Size; x++)
                {
                    Assert.True(seen.Add(Chunk.LocalIndex(x, y, z)));
                }
            }
        }

        Assert.Equal(Chunk.Volume, seen.Count);
    }

    [Fact]
    public void Vsechny_pozice_v_chunku_drzi_zapsanou_hodnotu()
    {
        var chunk = new Chunk(fill: 0);

        // Dva různé bloky ve vzoru šachovnice ověří, že se indexy nepletou mezi slovy.
        for (int y = 0; y < Chunk.Size; y++)
        {
            for (int z = 0; z < Chunk.Size; z++)
            {
                for (int x = 0; x < Chunk.Size; x++)
                {
                    chunk.SetBlock(x, y, z, (ushort)(((x + y + z) % 2) + 1));
                }
            }
        }

        for (int y = 0; y < Chunk.Size; y++)
        {
            for (int z = 0; z < Chunk.Size; z++)
            {
                for (int x = 0; x < Chunk.Size; x++)
                {
                    Assert.Equal((ushort)(((x + y + z) % 2) + 1), chunk.GetBlock(x, y, z));
                }
            }
        }
    }
}
