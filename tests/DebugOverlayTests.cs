using System.Globalization;
using OpenTK.Mathematics;
using Tesseris.Game;
using Tesseris.Game.UI;
using Xunit;

namespace Tesseris.Tests;

/// <summary>
/// Testy sazby overlaye a parsovĂˇnĂ­ argumentĹŻ.
///
/// KulturnĂ­ testy jsou tu zĂˇmÄ›rnÄ›: vĂ˝voj bÄ›ĹľĂ­ na ÄŤeskĂ˝ch Windows, kde je desetinnĂ˝ oddÄ›lovaÄŤ
/// ÄŤĂˇrka. Bez vynucenĂ© invariantnĂ­ kultury by se "FPS: 142.7" vysĂˇzelo jako "FPS: 142,7".
/// TestovacĂ­ projekt proto NESMĂŤ mĂ­t InvariantGlobalization â€” jinak by tyhle testy proĹˇly
/// i po odstranÄ›nĂ­ ochrany, kvĹŻli kterĂ© vznikly.
/// </summary>
public sealed class DebugOverlayTests
{
    [Fact]
    public void Radky_pouzivaji_desetinnou_tecku_i_na_ceske_kulture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("cs-CZ");

            Assert.Equal("FPS: 143   nejhorsi: 30", DebugOverlay.FpsLine(142.7, 33.3));
            Assert.Equal("Pos: -12.34 / 68.00 / 5.19", DebugOverlay.PositionLine(new Vector3(-12.34f, 68f, 5.19f)));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void Ceska_kultura_by_bez_ochrany_dala_carku()
    {
        // Kontrola, Ĺľe si test vĂ˝Ĺˇe neovÄ›Ĺ™uje samozĹ™ejmost: na cs-CZ se ÄŤĂˇrka opravdu pouĹľije.
        var czech = new CultureInfo("cs-CZ");

        Assert.Equal("142,7", 142.7.ToString("F1", czech));
    }

    [Fact]
    public void Fps_se_zaokrouhluje_na_cele_cislo()
    {
        Assert.Equal("FPS: 60   nejhorsi: 60", DebugOverlay.FpsLine(59.98, 16.67));
        Assert.Equal("FPS: 0   nejhorsi: 0", DebugOverlay.FpsLine(0.0, 0.0));
    }

    [Fact]
    public void Pozice_ma_dve_desetinna_mista_na_kazdou_osu()
    {
        Assert.Equal("Pos: 0.00 / 0.00 / 0.00", DebugOverlay.PositionLine(Vector3.Zero));
    }

    [Fact]
    public void Radek_draw_callu_obsahuje_cislo()
    {
        Assert.Equal("Draw calls: 0", DebugOverlay.DrawCallLine(0));
        Assert.Equal("Draw calls: 2", DebugOverlay.DrawCallLine(2));
    }

    [Fact]
    public void Vsechny_znaky_overlaye_jsou_ve_fontu()
    {
        string[] lines =
        [
            DebugOverlay.FpsLine(142.7, 33.3),
            DebugOverlay.PositionLine(new Vector3(-12.34f, 68f, 5.19f)),
            DebugOverlay.DrawCallLine(37),
        ];

        foreach (string line in lines)
        {
            foreach (char c in line)
            {
                Assert.True(
                    c >= Engine.Rendering.Font8x8.FirstChar && c <= Engine.Rendering.Font8x8.LastChar,
                    $"Znak '{c}' nenĂ­ v rozsahu fontu, v overlay by se nevykreslil.");
            }
        }
    }

    [Fact]
    public void Bez_argumentu_neni_selftest()
    {
        Assert.True(Program.TryParseSelftestFrames([], out int frames, out string? error));
        Assert.Equal(0, frames);
        Assert.Null(error);
    }

    [Fact]
    public void Selftest_prebira_pocet_framu()
    {
        Assert.True(Program.TryParseSelftestFrames(["--selftest=240"], out int frames, out _));
        Assert.Equal(240, frames);
    }

    [Theory]
    [InlineData("--selftest=0")]
    [InlineData("--selftest=-5")]
    [InlineData("--selftest=abc")]
    [InlineData("--selftest=")]
    [InlineData("--neznamy")]
    public void Vadny_argument_je_odmitnuty_s_hlaskou(string arg)
    {
        Assert.False(Program.TryParseSelftestFrames([arg], out _, out string? error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }
}

