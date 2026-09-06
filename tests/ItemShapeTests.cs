using OpenTK.Mathematics;
using Tesseris.Engine.Rendering;
using Tesseris.Game.World;
using Xunit;

namespace Tesseris.Tests;

/// <summary>
/// Vytažení tělesa předmětu z jeho ploché ikony.
/// </summary>
/// <remarks>
/// <para>Meč, ingot ani klacek nejsou plachta. Předměty na zemi se kreslily jako dvě
/// zkřížené karty — z každého úhlu papír a z boku dvě čáry. Těleso vzniká tak, že se
/// z obrysu ikony udělá deska s bočními stěnami všude, kde kresba končí.</para>
///
/// <para>Je to čistá geometrie, takže se dá otestovat celá: kolik stěn vznikne, kde leží
/// a jestli se počítají boky i uvnitř tělesa, kde jsou k ničemu.</para>
/// </remarks>
public sealed class ItemShapeTests
{
    private const float Half = 0.16f;
    private const float Thickness = 0.04f;

    private static int Size => TextureArray.ArtSize;

    /// <summary>Prázdná maska se zaplněnými texely podle předpisu.</summary>
    private static byte[] Mask(params (int Column, int Row)[] filled)
    {
        var mask = new byte[Size * Size];

        foreach ((int column, int row) in filled)
        {
            mask[(row * Size) + column] = 1;
        }

        return mask;
    }

    /// <summary>
    /// Jeden texel dá krychličku: přední a zadní stěna a čtyři boky.
    /// </summary>
    /// <remarks>
    /// Nejmenší možný případ, na kterém je vidět, že se boky opravdu dělají. Kdyby se
    /// nedělaly, zůstala by z předmětu zase jen deska bez hran.
    /// </remarks>
    [Fact]
    public void Jeden_texel_da_krychlicku()
    {
        ItemShape shape = ItemShape.Extrude(Mask((8, 8)), Half, Thickness);

        Assert.Equal(6, shape.Faces.Length);
    }

    /// <summary>
    /// Uvnitř tělesa se boky nedělají.
    /// </summary>
    /// <remarks>
    /// Čtverec dva na dva má osm vnějších hran, ne šestnáct. Kdyby se počítaly i vnitřní,
    /// nesla by čepel meče stěny schované samy v sobě — nevidět a stojí to stejně.
    /// </remarks>
    [Fact]
    public void Uvnitr_telesa_se_boky_nedelaji()
    {
        ItemShape shape = ItemShape.Extrude(
            Mask((4, 4), (5, 4), (4, 5), (5, 5)), Half, Thickness);

        // Dvě ploché stěny plus osm vnějších hran.
        Assert.Equal(2 + 8, shape.Faces.Length);
    }

    /// <summary>Plná dlaždice má boky jen po obvodu.</summary>
    [Fact]
    public void Plna_dlazdice_ma_boky_jen_po_obvodu()
    {
        var mask = new byte[Size * Size];
        Array.Fill(mask, (byte)1);

        ItemShape shape = ItemShape.Extrude(mask, Half, Thickness);

        Assert.Equal(2 + (4 * Size), shape.Faces.Length);
    }

    /// <summary>
    /// Těleso se vejde do svých mezí.
    /// </summary>
    /// <remarks>
    /// Kdyby přetékalo, prorůstal by předmět ležící na zemi terénem a v ruce by lezl
    /// hráči do obrazu.
    /// </remarks>
    [Fact]
    public void Teleso_se_vejde_do_svych_mezi()
    {
        var mask = new byte[Size * Size];
        Array.Fill(mask, (byte)1);

        ItemShape shape = ItemShape.Extrude(mask, Half, Thickness);

        foreach (ItemShape.Face face in shape.Faces)
        {
            foreach (Vector3 point in new[] { face.P0, face.P1, face.P2, face.P3 })
            {
                Assert.InRange(point.X, -Half - 1e-4f, Half + 1e-4f);
                Assert.InRange(point.Y, -Half - 1e-4f, Half + 1e-4f);
                Assert.InRange(point.Z, (-Thickness * 0.5f) - 1e-4f, (Thickness * 0.5f) + 1e-4f);
            }
        }
    }

    /// <summary>
    /// Bok se vzorkuje jedním texelem, ne celou kresbou.
    /// </summary>
    /// <remarks>
    /// Bok je tenký proužek. Kdyby přes něj šla celá textura, byla by na něm zmenšená celá
    /// ikona; takhle má barvu toho texelu, ke kterému patří.
    /// </remarks>
    [Fact]
    public void Bok_se_vzorkuje_jednim_texelem()
    {
        ItemShape shape = ItemShape.Extrude(Mask((3, 11)), Half, Thickness);

        // První dvě stěny jsou plochy přes celou dlaždici, zbytek jsou boky.
        foreach (ItemShape.Face face in shape.Faces.Skip(2))
        {
            Assert.Equal(face.U0, face.U1);
            Assert.Equal(face.U0, face.U2);
            Assert.Equal(face.U0, face.U3);

            // Střed texelu, ne jeho roh.
            Assert.Equal(3.5f / Size, face.U0.X, 4);
            Assert.Equal(11.5f / Size, face.U0.Y, 4);
        }
    }

    /// <summary>Řádek nula je nahoře, stejně jako v textuře.</summary>
    [Fact]
    public void Radek_nula_je_nahore()
    {
        ItemShape top = ItemShape.Extrude(Mask((8, 0)), Half, Thickness);
        ItemShape bottom = ItemShape.Extrude(Mask((8, Size - 1)), Half, Thickness);

        float topY = top.Faces.Skip(2).Average(f => f.P0.Y);
        float bottomY = bottom.Faces.Skip(2).Average(f => f.P0.Y);

        Assert.True(topY > bottomY, "Řádek nula vyšel dole — obrázek by byl vzhůru nohama.");
    }

    /// <summary>
    /// Krátká maska nespadne, jen nedá nic.
    /// </summary>
    /// <remarks>
    /// Vrstva, pro kterou textura chybí, vrací prázdný obrys. Pád kvůli tomu by shodil hru
    /// při vypadnutí jednoho předmětu.
    /// </remarks>
    [Fact]
    public void Kratka_maska_nespadne()
    {
        Assert.Empty(ItemShape.Extrude([], Half, Thickness).Faces);
        Assert.Empty(ItemShape.Extrude(new byte[4], Half, Thickness).Faces);
    }

    /// <summary>
    /// Skutečná ikona nástroje dá rozumně velké těleso.
    /// </summary>
    /// <remarks>
    /// Horní mez je tu proto, aby si nikdo nespletl vytažení s převodem každého texelu na
    /// krychli: krumpáč má mít desítky stěn, ne tisíc.
    /// </remarks>
    [Theory]
    [InlineData("iron_pickaxe")]
    [InlineData("stick")]
    [InlineData("iron_ingot")]
    [InlineData("charcoal")]
    public void Skutecna_ikona_da_rozumne_teleso(string texture)
    {
        Span<byte> art = new byte[TextureArray.ArtSize * TextureArray.ArtSize * 4];
        TextureArray.GenerateTile(texture, art);

        var mask = new byte[TextureArray.ArtSize * TextureArray.ArtSize];

        for (int i = 0; i < mask.Length; i++)
        {
            mask[i] = art[(i * 4) + 3] >= 128 ? (byte)1 : (byte)0;
        }

        ItemShape shape = ItemShape.Extrude(mask, Half, Thickness);

        Assert.True(shape.Faces.Length > 6, $"'{texture}' dalo jen {shape.Faces.Length} stěn — ikona je prázdná?");
        Assert.True(shape.Faces.Length < 400, $"'{texture}' dalo {shape.Faces.Length} stěn, to je moc.");
    }
}
