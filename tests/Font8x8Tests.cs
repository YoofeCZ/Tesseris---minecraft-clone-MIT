using Tesseris.Engine.Rendering;
using Xunit;

namespace Tesseris.Tests;

/// <summary>
/// Strukturální kontrola dat fontu. Jde o regresní pojistku — data jsou v kódu, takže je
/// snadné je omylem posunout o bajt a rozsypat celý overlay.
/// </summary>
public sealed class Font8x8Tests
{
    /// <summary>Znaky, které podle dokumentace mají zasahovat do dolního dotahu (řádek 7).</summary>
    private const string DescenderChars = ",;_gjpqy";

    [Fact]
    public void Tabulka_ma_presne_ocekavanou_delku()
    {
        Assert.Equal(95, Font8x8.CharCount);
        Assert.Equal(Font8x8.CharCount * Font8x8.BytesPerGlyph, Font8x8.Glyphs.Length);
        Assert.Equal(760, Font8x8.Glyphs.Length);
    }

    [Fact]
    public void Bitova_konvence_je_bit_nula_vlevo()
    {
        // 'L' je pro tuhle kontrolu jediný rozumný glyf: má vodorovnou patku vlevo dole,
        // takže pod opačnou konvencí vyjde zrcadlené a test spadne.
        //
        // POZOR: 'A' se na tohle použít NEDÁ — je zrcadlově symetrická a vyjde stejně pod
        // oběma konvencemi, takže by test jen předstíral, že něco hlídá.
        int rowIndex = (('L' - Font8x8.FirstChar) * Font8x8.BytesPerGlyph) + 6;

        Assert.Equal(0x1F, Font8x8.Glyphs[rowIndex]);

        Assert.True(Font8x8.Pixel('L', 0, 6), "Levý dolní pixel 'L' musí být zapnutý.");
        Assert.True(Font8x8.Pixel('L', 4, 6), "Pravý konec patky 'L' musí být zapnutý.");
        Assert.True(Font8x8.Pixel('L', 0, 0), "Svislý dřík 'L' musí začínat vlevo nahoře.");
        Assert.False(Font8x8.Pixel('L', 4, 0), "Vpravo nahoře 'L' nic nemá.");
    }

    [Fact]
    public void Mezera_je_prazdna_a_zadny_jiny_glyf_neni()
    {
        for (int index = 0; index < Font8x8.CharCount; index++)
        {
            char c = (char)(Font8x8.FirstChar + index);
            bool empty = true;

            for (int y = 0; y < Font8x8.GlyphHeight; y++)
            {
                if (Font8x8.Glyphs[(index * Font8x8.BytesPerGlyph) + y] != 0)
                {
                    empty = false;
                    break;
                }
            }

            if (c == ' ')
            {
                Assert.True(empty, "Mezera musí být prázdná.");
            }
            else
            {
                Assert.False(empty, $"Glyf '{c}' (ASCII {(int)c}) je prázdný — chybějící data.");
            }
        }
    }

    [Fact]
    public void Kresba_nepresahuje_sirku_peti_pixelu()
    {
        // Sloupce 5..7 tvoří mezeru mezi znaky. Kdyby do nich glyf zasáhl, písmena by se slila.
        foreach (byte row in Font8x8.Glyphs)
        {
            Assert.True(row <= 0x1F, $"Řádek 0x{row:X2} zasahuje za sloupec 4.");
        }
    }

    [Fact]
    public void Dolni_dotah_maji_presne_zdokumentovane_znaky()
    {
        for (int index = 0; index < Font8x8.CharCount; index++)
        {
            char c = (char)(Font8x8.FirstChar + index);
            byte lastRow = Font8x8.Glyphs[(index * Font8x8.BytesPerGlyph) + 7];
            bool expected = DescenderChars.Contains(c, StringComparison.Ordinal);

            Assert.True(
                expected == (lastRow != 0),
                $"Znak '{c}' má dolní dotah = {lastRow != 0}, čekalo se {expected}.");
        }
    }

    [Fact]
    public void Radkovani_nechava_misto_na_dolni_dotah()
    {
        // Kdyby se řádky sázely s roztečí přesně 8, dotah 'y' by se dotkl verzálky pod ním.
        Assert.True(
            TextRenderer.LineHeight > Font8x8.GlyphHeight,
            "Rozteč řádků musí být větší než výška buňky.");
    }

    [Fact]
    public void Pixel_mimo_rozsah_vraci_false_a_nespadne()
    {
        Assert.False(Font8x8.Pixel('A', -1, 0));
        Assert.False(Font8x8.Pixel('A', 0, 99));
        Assert.False(Font8x8.Pixel('', 0, 0)); // těsně pod FirstChar
        Assert.False(Font8x8.Pixel('ÿ', 0, 0)); // nad LastChar
    }
}
