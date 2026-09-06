using Tesseris.Game.UI;
using Xunit;

namespace Tesseris.Tests;

/// <summary>
/// Posuvníky v panelu obrazu (F9). Pruh je jediné, co o hodnotě vypovídá na první pohled —
/// když ukazuje jinam než skutečná hodnota, ladí se naslepo.
/// </summary>
public sealed class DebugMenuSliderTests
{
    [Fact]
    public void Slider_fills_proportionally_to_value_in_its_range()
    {
        var menu = new DebugMenu();
        float value = 0.5f;
        menu.AddFloat("Sytost", () => value, v => value = v, 0.1f, 0f, 1f);

        string line = Assert.Single(menu.SliderLines(labelWidth: 6, barWidth: 10));

        Assert.Equal("> Sytost -[#####-----]+ 0.50", line);
    }

    [Fact]
    public void Slider_reaches_both_ends_of_its_range()
    {
        var menu = new DebugMenu();
        float value = 0f;
        menu.AddFloat("Jas", () => value, v => value = v, 1f, 0f, 4f);

        Assert.Contains("[----------]", Assert.Single(menu.SliderLines(barWidth: 10)));

        value = 4f;
        Assert.Contains("[##########]", Assert.Single(menu.SliderLines(barWidth: 10)));
    }

    /// <summary>
    /// Rozsah nezačínající nulou je v panelu běžný — kontrast jde od 0,5. Kdyby se podíl
    /// počítal jako hodnota/maximum, ukazoval by pruh na minimu čtvrtinu místo prázdna.
    /// </summary>
    [Fact]
    public void Slider_measures_from_the_minimum_not_from_zero()
    {
        var menu = new DebugMenu();
        float value = PlayerOptions.MinContrast;
        menu.AddFloat(
            "Kontrast",
            () => value,
            v => value = v,
            0.02f,
            PlayerOptions.MinContrast,
            PlayerOptions.MaxContrast);

        Assert.Contains("[----------]", Assert.Single(menu.SliderLines(barWidth: 10)));
    }

    [Fact]
    public void Arrow_step_changes_the_value_and_shift_multiplies_it()
    {
        var menu = new DebugMenu();
        float value = 1f;
        menu.AddFloat("Sytost", () => value, v => value = v, 0.1f, 0f, 10f);

        menu.Adjust(1, 1);
        Assert.Equal(1.1f, value, 3);

        menu.Adjust(1, 10);
        Assert.Equal(2.1f, value, 3);

        menu.Adjust(-1, 1);
        Assert.Equal(2.0f, value, 3);
    }

    [Fact]
    public void Value_never_leaves_its_range_however_long_the_arrow_is_held()
    {
        var menu = new DebugMenu();
        float value = 1f;
        menu.AddFloat("Sytost", () => value, v => value = v, 0.5f, 0f, 2f);

        for (int i = 0; i < 50; i++)
        {
            menu.Adjust(1, 10);
        }

        Assert.Equal(2f, value);

        for (int i = 0; i < 50; i++)
        {
            menu.Adjust(-1, 10);
        }

        Assert.Equal(0f, value);
    }

    /// <summary>
    /// Nadpis oddílu a naměřená hodnota nemají rozsah. Pruh u nich nesmí být — tvrdil by,
    /// že se s nimi dá hýbat.
    /// </summary>
    [Fact]
    public void Rows_without_a_range_get_no_slider()
    {
        var menu = new DebugMenu();
        menu.AddReadOnly("--- barvy ---", () => string.Empty);

        string line = Assert.Single(menu.SliderLines(labelWidth: 4, barWidth: 10));

        Assert.DoesNotContain('#', line);
        Assert.DoesNotContain('[', line);
    }

    [Fact]
    public void Toggle_shows_a_full_or_empty_bar()
    {
        var menu = new DebugMenu();
        bool value = false;
        menu.AddToggle("Zare", () => value, v => value = v);

        Assert.Contains("[----------]", Assert.Single(menu.SliderLines(barWidth: 10)));
        Assert.Contains("vypnuto", Assert.Single(menu.SliderLines(barWidth: 10)));

        menu.Adjust(1, 1);

        Assert.Contains("[##########]", Assert.Single(menu.SliderLines(barWidth: 10)));
        Assert.Contains("zapnuto", Assert.Single(menu.SliderLines(barWidth: 10)));
    }

    /// <summary>
    /// Font overlaye pokrývá ASCII 32–126 a nic víc. Písmeno s diakritikou se nevykreslí
    /// vůbec, takže by v popisku vznikla díra.
    /// </summary>
    [Fact]
    public void Slider_lines_stay_inside_the_overlay_font()
    {
        var menu = new DebugMenu();
        float value = 0.25f;
        menu.AddFloat("Videt v noci", () => value, v => value = v, 0.02f, 0f, 1f);
        menu.AddToggle("Cas bezi", () => false, _ => { });
        menu.AddReadOnly("--- noc ---", () => string.Empty);

        foreach (string line in menu.SliderLines())
        {
            Assert.All(line, c => Assert.InRange(c, ' ', '~'));
        }
    }

    /// <summary>
    /// Uložené meze musí odpovídat mezím posuvníků. Kdyby byly širší, dal by se uložit
    /// stav, na který se posuvníkem nedá vrátit; kdyby užší, posuvník by šel na hodnotu,
    /// kterou uložení hned zahodí.
    /// </summary>
    [Fact]
    public void Saved_colour_settings_are_clamped_to_the_slider_ranges()
    {
        var options = new PlayerOptions
        {
            Exposure = 99f,
            ColorSaturation = -5f,
            ColorContrast = 99f,
            SplitTone = -1f,
            BloomThreshold = 99f,
            BloomStrength = -1f,
            BloomRadius = 0f,
        };

        options.Clamp();

        Assert.Equal(PlayerOptions.MaxExposure, options.Exposure);
        Assert.Equal(PlayerOptions.MinSaturation, options.ColorSaturation);
        Assert.Equal(PlayerOptions.MaxContrast, options.ColorContrast);
        Assert.Equal(PlayerOptions.MinSplitTone, options.SplitTone);
        Assert.Equal(PlayerOptions.MaxBloomThreshold, options.BloomThreshold);
        Assert.Equal(PlayerOptions.MinBloomStrength, options.BloomStrength);
        Assert.Equal(PlayerOptions.MinBloomRadius, options.BloomRadius);
    }

    /// <summary>
    /// Výchozí hodnoty v uloženém nastavení musí sedět na ty v rendereru. Kdyby se lišily,
    /// první spuštění by obraz změnilo, aniž by o to kdokoli požádal.
    /// </summary>
    [Fact]
    public void Default_colour_settings_match_the_renderer_defaults()
    {
        var options = new PlayerOptions();

        Assert.True(options.ColorGrading);
        Assert.Equal(1.05f, options.Exposure, 3);
        Assert.Equal(1.28f, options.ColorSaturation, 3);
        Assert.Equal(1.16f, options.ColorContrast, 3);
        Assert.Equal(0.55f, options.SplitTone, 3);
        Assert.Equal(1.0f, options.BloomThreshold, 3);
        Assert.Equal(0.40f, options.BloomStrength, 3);
        Assert.Equal(3.0f, options.BloomRadius, 3);
    }
}
