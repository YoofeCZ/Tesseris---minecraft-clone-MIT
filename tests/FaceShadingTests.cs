using System;
using System.Collections.Generic;
using System.Linq;
using Tesseris.Engine.Rendering;
using Xunit;

namespace Tesseris.Tests;

/// <summary>
/// Testy jasu stěn. Vzniklo z toho, že krajina se správným tvarem vypadala jako placka,
/// protože každá plocha měla stejný jas.
/// </summary>
public sealed class FaceShadingTests
{
    private static IEnumerable<float> VsechnySteny()
    {
        foreach (int axis in new[] { 0, 1, 2 })
        {
            foreach (bool positive in new[] { true, false })
            {
                yield return FaceShading.ForAxis(axis, positive);
            }
        }
    }

    [Fact]
    public void Vrsek_je_nejsvetlejsi_a_spodek_nejtmavsi()
    {
        // Dřív se tu porovnávalo pevné pořadí vršek > sever/jih > východ/západ > spodek.
        // Se sluncem mimo osy takové pořadí neplatí — přisvícená západní stěna může být
        // světlejší než odvrácená jižní. Co platit musí, je jen krajní dvojice.
        float[] steny = VsechnySteny().ToArray();

        Assert.Equal(steny.Max(), FaceShading.ForAxis(1, positive: true), 4);
        Assert.Equal(steny.Min(), FaceShading.ForAxis(1, positive: false), 4);
    }

    [Fact]
    public void Tabulka_odpovida_Luanti()
    {
        // POZOR: tenhle test tvrdi PRAVY OPAK toho, co tu stalo driv.
        //
        // Puvodni test zadal, aby zadne dve steny nesvitily stejne — jas se pocital
        // z dot(normala, smer ke slunci), takze vychodni svah kopce vysel jinak nez
        // zapadni. Znelo to dobre a bylo to spatne: jas se peče do vrcholu pri
        // meshovani, ale slunce se pohybuje. Chunk premeshovany v poledne mel proto jine
        // stinovani nez soused premeshovany za soumraku a mezi nimi byl videt sev.
        //
        // Luanti to resi tabulkou sesti pevnych cisel, ktera na slunci nezavisi:
        // odmocniny z 1,0 / 0,2 / 0,45 / 0,7. Putujici slunce ma na starosti stinova
        // mapa, ktera se pocita kazdy snimek. Viz src/client/mesh.cpp, applyFacesShading.
        Assert.Equal(1.000000f, FaceShading.ForAxis(1, positive: true), 5);
        Assert.Equal(0.447213f, FaceShading.ForAxis(1, positive: false), 5);
        Assert.Equal(0.670820f, FaceShading.ForAxis(0, positive: true), 5);
        Assert.Equal(0.670820f, FaceShading.ForAxis(0, positive: false), 5);
        Assert.Equal(0.836660f, FaceShading.ForAxis(2, positive: true), 5);
        Assert.Equal(0.836660f, FaceShading.ForAxis(2, positive: false), 5);
    }

    [Fact]
    public void Vodorovna_plocha_je_proti_svisle_dost_svetla_na_cteni_tvaru()
    {
        // Reliéf uz nedela rozdil mezi ctyrmi svislymi stenami, ale pomer vodorovne
        // plochy ke svisle. Kdyby klesl k jednicce, byla by krajina zase placka.
        float top = FaceShading.ForAxis(1, positive: true);
        float sideX = FaceShading.ForAxis(0, positive: true);
        float bottom = FaceShading.ForAxis(1, positive: false);

        Assert.True(top / sideX > 1.4f, $"Vrsek je jen {top / sideX:F2}x svetlejsi nez stena.");
        Assert.True(top / bottom > 2.0f, $"Vrsek je jen {top / bottom:F2}x svetlejsi nez spodek.");
    }

    [Fact]
    public void Vrsek_ma_plny_jas()
    {
        Assert.Equal(1f, FaceShading.ForAxis(1, positive: true), 4);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Vysledek_zustava_v_rozumnem_rozsahu(int occlusion)
    {
        foreach (int axis in new[] { 0, 1, 2 })
        {
            foreach (bool positive in new[] { true, false })
            {
                float shade = FaceShading.Combine(occlusion, FaceShading.ForAxis(axis, positive));

                Assert.InRange(shade, 0.1f, 1f);
            }
        }
    }

    [Fact]
    public void Volny_roh_na_vrsku_sviti_naplno()
    {
        Assert.Equal(1f, FaceShading.Combine(3, FaceShading.ForAxis(1, true)), 4);
    }

    [Fact]
    public void Vetsi_zastineni_znamena_tmavsi_roh()
    {
        float brightness = FaceShading.ForAxis(1, true);

        float open = FaceShading.Combine(3, brightness);
        float partial = FaceShading.Combine(1, brightness);
        float closed = FaceShading.Combine(0, brightness);

        Assert.True(open > partial);
        Assert.True(partial > closed);
    }

    [Fact]
    public void Ani_uplne_zastineny_roh_nezcerna()
    {
        // Spodní mez existuje proto, aby zapadlé rohy nebyly úplně černé díry.
        float darkest = FaceShading.Combine(0, FaceShading.ForAxis(1, positive: false));

        Assert.True(darkest > 0.1f, $"Nejtmavší možný roh má jas {darkest:F3}, což je moc málo.");
    }

    [Fact]
    public void Jas_steny_prevazi_nad_stinenim_rohu()
    {
        // Nejtmavší roh na vršku musí být pořád světlejší než nejsvětlejší roh na spodku.
        // Kdyby ne, spodní strany převisů by se míchaly s vrchními a tvar by se ztratil.
        float darkestTop = FaceShading.Combine(0, FaceShading.ForAxis(1, true));
        float brightestBottom = FaceShading.Combine(3, FaceShading.ForAxis(1, false));

        // ZNAMENKO BYLO OBRACENE PROTI VLASTNIMU POPISU. Test se jmenuje „jas steny
        // prevazi nad stinenim rohu" a komentar nad nim zada, aby byl nejtmavsi roh na
        // vrsku porad svetlejsi nez nejsvetlejsi roh na spodku — ale tvrdil opak.
        // S hodnotami z Luanti vychazi 0,463 proti 0,447, tedy presne to, oc tu jde.
        Assert.True(darkestTop > brightestBottom,
            $"Nejtmavsi roh na vrsku ma {darkestTop:F3}, nejsvetlejsi na spodku "
            + $"{brightestBottom:F3} — stineni rohu prebilo jas steny a tvar previsu se ztrati.");
    }
}
