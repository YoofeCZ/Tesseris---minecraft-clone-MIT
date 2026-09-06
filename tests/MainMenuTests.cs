using Tesseris.Game.UI;
using Xunit;

namespace Tesseris.Tests;

public sealed class MainMenuTests
{
    [Fact]
    public void Vychozi_menu_ma_pozadovane_hodnoty()
    {
        var menu = new MainMenu();

        Assert.Equal("novy-svet", menu.NameText);
        Assert.Equal(string.Empty, menu.SeedText);
        Assert.Equal(MainMenuField.Name, menu.FocusedField);
        Assert.Equal(MainMenu.AutomaticPregenRadius, menu.PregenRadius);
        Assert.Equal(StartupLanguage.Czech, menu.Language);
    }

    [Fact]
    public void Jazyk_meni_validaci_a_anglictina_je_pripravena_pro_uvodni_obrazovku()
    {
        var menu = new MainMenu(string.Empty, string.Empty);

        menu.SetLanguage(StartupLanguage.English);

        Assert.Equal(StartupLanguage.English, menu.Language);
        Assert.False(menu.TryCreateRequest(1, out _, out string error));
        Assert.Equal("Enter a world name.", error);
        Assert.Contains("F3  DEBUG OVERLAY", StartupLocalization.For(StartupLanguage.English).ControlsLines);
    }

    [Fact]
    public void Anglictina_ma_i_herni_popisky_mimo_uvodni_obrazovku()
    {
        Assert.Equal("INVENTORY", GameLocalization.Inventory(StartupLanguage.English));
        Assert.Equal("RECIPE BOOK", GameLocalization.RecipeBook(StartupLanguage.English));
        Assert.Equal("small stone", GameLocalization.ItemName(
            new Tesseris.Game.Items.ItemDefinition { Id = "tesseris:small_stone", Name = "kaminek" },
            StartupLanguage.English));

        Assert.Contains(StartupLocalization.For(StartupLanguage.English).FirstStepsLines,
            line => line.Contains("FLINT PICKAXE", StringComparison.Ordinal));
        Assert.Contains(StartupLocalization.For(StartupLanguage.Czech).FirstStepsLines,
            line => line.Contains("PAZOURKOVY KRUMPAC", StringComparison.Ordinal));
    }

    [Fact]
    public void Navod_popisuje_skutecny_postup_pres_kamennou_pec()
    {
        string[] czech = StartupLocalization.For(StartupLanguage.Czech).FirstStepsLines;
        string[] english = StartupLocalization.For(StartupLanguage.English).FirstStepsLines;

        Assert.Contains("Z DLAZBY VYROB KAMENNE NASTROJE.", czech);
        Assert.Contains("Z 8 KOSTEK VYROB KAMENNOU PEC.", czech);
        Assert.Contains("PEC S PALIVEM TAVI ZELEZO A DALSI RUDY.", czech);
        Assert.Contains("CRAFT STONE TOOLS DIRECTLY FROM COBBLESTONE.", english);
        Assert.Contains("CRAFT A STONE FURNACE FROM 8 COBBLESTONE.", english);
        Assert.Contains("USE FUEL TO SMELT IRON AND OTHER ORES.", english);
        Assert.DoesNotContain(czech,
            line => line.Contains("Z VYTEZENEHO KAMENE VYROB KAMENNE NASTROJE", StringComparison.Ordinal));
    }

    [Fact]
    public void Text_se_pise_jen_do_aktivniho_pole()
    {
        var menu = new MainMenu("svet", "");

        menu.AppendText(" 2");
        menu.FocusNext();
        menu.AppendText("textovy-seed");

        Assert.Equal("svet 2", menu.NameText);
        Assert.Equal("textovy-seed", menu.SeedText);
        Assert.Equal(MainMenuField.Seed, menu.FocusedField);
    }

    [Fact]
    public void Prvni_psani_nahradi_vychozi_navrh_nazvu()
    {
        var menu = new MainMenu();

        menu.AppendText("ostrov");

        Assert.Equal("ostrov", menu.NameText);
    }

    [Fact]
    public void Backspace_smaze_cely_unicode_znak()
    {
        var menu = new MainMenu("A😀", "");

        menu.Backspace();

        Assert.Equal("A", menu.NameText);
    }

    [Fact]
    public void Vlozeny_text_se_orizne_na_64_unicode_znaku()
    {
        var menu = new MainMenu(string.Empty, string.Empty);

        menu.AppendText(string.Concat(Enumerable.Repeat("😀", 70)));
        menu.Focus(MainMenuField.Seed);
        menu.AppendText(new string('x', 80));

        Assert.Equal(64, System.Globalization.StringInfo.ParseCombiningCharacters(menu.NameText).Length);
        Assert.Equal(64, menu.SeedText.Length);
        Assert.EndsWith("😀", menu.NameText, StringComparison.Ordinal);
    }

    [Fact]
    public void Tab_prepne_pole_a_predgenerace_je_vzdy_cely_dohled()
    {
        var menu = new MainMenu();

        menu.FocusNext();
        Assert.Equal(MainMenuField.Seed, menu.FocusedField);
        Assert.Equal(10, menu.PregenRadius);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("..")]
    [InlineData("slozka/svet")]
    public void Neplatny_nazev_nevytvori_pozadavek(string name)
    {
        var menu = new MainMenu(name, "123");

        Assert.False(menu.TryCreateRequest(99, out _, out string error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void Ciselny_seed_je_invariantni_a_prazdny_pouzije_dodanou_nahodu()
    {
        Assert.Equal(-214, MainMenu.ResolveSeed("  -214  ", 7));
        Assert.Equal(7, MainMenu.ResolveSeed("  ", 7));
    }

    [Fact]
    public void Textovy_seed_pouziva_stabilni_fnv1a()
    {
        // Známý FNV-1a vektor: výsledek nesmí záviset na procesu ani GetHashCode.
        Assert.Equal(1_335_831_723, MainMenu.ResolveSeed("hello", 17));
        Assert.Equal(
            MainMenu.ResolveSeed("žlutý svět", 1),
            MainMenu.ResolveSeed("žlutý svět", 999));
    }

    [Fact]
    public void Platny_pozadavek_ma_oriznuty_nazev_a_seed()
    {
        var menu = new MainMenu("  Moje země  ", "ostrov");

        bool valid = menu.TryCreateRequest(123, out WorldCreationRequest request, out string error);

        Assert.True(valid, error);
        Assert.Equal("Moje země", request.Name);
        Assert.Equal(MainMenu.ResolveSeed("ostrov", 0), request.Seed);
    }
}
