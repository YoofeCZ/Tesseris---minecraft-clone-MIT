using OpenTK.Mathematics;
using Tesseris.Game.Blocks;
using Tesseris.Game.Items;
using Tesseris.Game.UI;
using Xunit;

namespace Tesseris.Tests;

/// <summary>
/// Rozvržení panelu inventáře a to, co kam patří pod kurzorem.
/// </summary>
/// <remarks>
/// <para><b>Proč na to testy.</b> Pravý sloupec okna má tři obsahy — výrobu, mřížku všech
/// předmětů v kreativu a pec — a každý z nich má vlastní zkoušku zásahu. Když se některá
/// nezeptá, jestli je vůbec na řadě, spolkne kliky té, která tam právě je. Navenek to
/// nevypadá jako chyba v rozvržení, ale jako že „inventář nereaguje", takže se to hledá
/// úplně jinde.</para>
///
/// <para>Nic z toho nepotřebuje grafiku: jsou to čísla.</para>
/// </remarks>
public sealed class InventoryScreenTests
{
    private const int Width = 1280;
    private const int Height = 720;

    private static ItemRegistry Items()
    {
        BlockRegistry blocks = BlockRegistry.LoadFromDirectory(
            Path.Combine(AppContext.BaseDirectory, "assets", "blocks"));

        return ItemRegistry.Create(blocks, Path.Combine(AppContext.BaseDirectory, "assets", "items"));
    }

    private static InventoryScreen Screen() => new(Items()) { Open = true };

    /// <summary>Střed první buňky mřížky předmětů.</summary>
    private static Vector2 FirstCreativeCell()
    {
        (float x, float y, float w, _) = InventoryScreen.Window(Width, Height);
        float size = InventoryScreen.SlotSize(Height);

        float cell = (size * 4.9f) / 5f;

        return new Vector2(x + w - (size * 5.2f) + (cell * 0.5f), y + (size * 1.9f) + (cell * 0.5f));
    }

    [Fact]
    public void Creative_hledani_filtruje_podle_nekolika_casti_nazvu()
    {
        ItemRegistry items = Items();
        var screen = new InventoryScreen(items)
        {
            Open = true,
            Creative = true,
            SearchFocused = true,
        };

        screen.CreativeScroll = 4;
        screen.AppendSearchText("oak stairs");

        int result = screen.CreativeItemAt(0);
        Assert.NotEqual(ItemRegistry.Nothing, result);
        Assert.Equal("tesseris:oak_planks_stairs", items.Definition(result).Id);
        Assert.Equal(0, screen.CreativeScroll);
        Assert.Equal(1, screen.CreativeRowCount);
    }

    [Fact]
    public void Hledani_ignoruje_diakritiku_a_delete_ho_vycisti()
    {
        ItemRegistry items = Items();
        var screen = new InventoryScreen(items)
        {
            Open = true,
            Creative = true,
            SearchFocused = true,
        };

        screen.AppendSearchText("sazenice");
        int result = screen.CreativeItemAt(0);
        Assert.NotEqual(ItemRegistry.Nothing, result);
        Assert.Contains("sapling", items.Definition(result).Id, StringComparison.Ordinal);

        screen.ClearSearch();
        Assert.Equal(string.Empty, screen.SearchText);
        Assert.True(screen.CreativeRowCount > 1);
    }

    [Fact]
    public void Receptar_hleda_vystup_a_vraci_skutecny_index_receptu()
    {
        ItemRegistry items = Items();
        RecipeBook recipes = RecipeBook.Load(
            items, Path.Combine(AppContext.BaseDirectory, "assets", "recipes"));
        var screen = new InventoryScreen(items)
        {
            Open = true,
            BookOpen = true,
            SearchFocused = true,
        };

        screen.AppendSearchText("oak stairs");
        Assert.True(screen.FilteredRecipeCount(recipes) > 0);

        float size = InventoryScreen.SlotSize(Height);
        (float x, float y, float w, _) = InventoryScreen.Window(Width, Height);
        float cell = (size * 4.9f) / InventoryScreen.BookColumns;
        var first = new Vector2(
            x + w - (size * 5.2f) + (cell * 0.5f),
            y + (size * 1.9f) + (cell * 0.5f));

        int recipeIndex = screen.BookRecipeAt(first, recipes, Width, Height);
        Assert.True(recipeIndex >= 0);
        Assert.Equal(
            "tesseris:oak_planks_stairs",
            items.Definition(recipes.Crafting[recipeIndex].Output).Id);
    }

    /// <summary>
    /// V kreativu nesmí výroba brát kliky do mřížky předmětů.
    /// </summary>
    /// <remarks>
    /// <b>Přesně tohle bylo rozbité.</b> Výroba hlídala celou plochu pravého sloupce bez
    /// ohledu na to, co v něm zrovna je, takže v kreativu spolkla klik a pokusila se místo
    /// toho něco vyrobit. Klikatelné zůstaly jen ty řádky mřížky, které přetékaly pod okno.
    /// </remarks>
    [Fact]
    public void V_kreativu_vyroba_kliky_nebere()
    {
        InventoryScreen screen = Screen();
        screen.Creative = true;

        Vector2 cell = FirstCreativeCell();

        Assert.Equal(-1, screen.RecipeAt(cell, Width, Height));
        Assert.NotEqual(ItemRegistry.Nothing, screen.CreativeAt(cell, Width, Height));
    }

    /// <summary>U otevřené pece nesmí výroba brát kliky do jejích slotů.</summary>
    [Fact]
    public void U_pece_vyroba_kliky_nebere()
    {
        InventoryScreen screen = Screen();
        screen.Furnace = new Furnace();

        float size = InventoryScreen.SlotSize(Height);

        for (int index = 0; index < InventoryScreen.FurnaceSlots; index++)
        {
            Vector2 corner = screen.FurnaceSlot(index, Width, Height);
            var centre = new Vector2(corner.X + (size * 0.5f), corner.Y + (size * 0.5f));

            Assert.Equal(-1, screen.RecipeAt(centre, Width, Height));
            Assert.Equal(index, screen.FurnaceSlotAt(centre, Width, Height));
        }
    }

    /// <summary>
    /// Celá pec se vejde do okna, včetně vypínače.
    /// </summary>
    /// <remarks>
    /// Vypínač se lepil napravo od paliva a vylezl tím ven z rámu — na obrázku visel na
    /// krajině vedle okna. Test hlídá všechny sloty i tlačítko naráz, protože přetéct může
    /// kterýkoli z nich a na jednom rozlišení to nemusí být vidět.
    /// </remarks>
    [Theory]
    [InlineData(1280, 720)]
    [InlineData(1920, 1080)]
    [InlineData(2560, 1440)]
    [InlineData(800, 600)]
    public void Pec_se_vejde_do_okna(int width, int height)
    {
        InventoryScreen screen = Screen();
        screen.Furnace = new Furnace();

        float size = InventoryScreen.SlotSize(height);
        (float wx, float wy, float ww, float wh) = InventoryScreen.Window(width, height);

        for (int index = 0; index < InventoryScreen.FurnaceSlots; index++)
        {
            Vector2 corner = screen.FurnaceSlot(index, width, height);

            Assert.True(corner.X >= wx, $"Slot {index} leze vlevo ven z okna.");
            Assert.True(corner.X + size <= wx + ww, $"Slot {index} leze vpravo ven z okna.");
            Assert.True(corner.Y >= wy, $"Slot {index} leze nad okno.");
            Assert.True(corner.Y + size <= wy + wh, $"Slot {index} leze pod okno.");
        }

        (float bx, float by, float bw, float bh) = screen.FurnaceButton(width, height);

        Assert.True(bx >= wx && bx + bw <= wx + ww, "Vypínač leze vodorovně ven z okna.");
        Assert.True(by >= wy && by + bh <= wy + wh, "Vypínač leze svisle ven z okna.");
    }

    /// <summary>Sloty pece se navzájem nepřekrývají — jinak by klik trefil jiný, než na který míří.</summary>
    [Fact]
    public void Sloty_pece_se_neprekryvaji()
    {
        InventoryScreen screen = Screen();
        screen.Furnace = new Furnace();

        float size = InventoryScreen.SlotSize(Height);

        for (int a = 0; a < InventoryScreen.FurnaceSlots; a++)
        {
            for (int b = a + 1; b < InventoryScreen.FurnaceSlots; b++)
            {
                Vector2 first = screen.FurnaceSlot(a, Width, Height);
                Vector2 second = screen.FurnaceSlot(b, Width, Height);

                bool apart = MathF.Abs(first.X - second.X) >= size
                             || MathF.Abs(first.Y - second.Y) >= size;

                Assert.True(apart, $"Sloty pece {a} a {b} se překrývají.");
            }
        }
    }

    /// <summary>Vypínač pece je pod kurzorem tam, kde se kreslí, a nepřekrývá se se slotem.</summary>
    [Fact]
    public void Vypinac_pece_je_tam_kde_se_kresli()
    {
        InventoryScreen screen = Screen();
        screen.Furnace = new Furnace();

        (float x, float y, float w, float h) = screen.FurnaceButton(Width, Height);
        var centre = new Vector2(x + (w * 0.5f), y + (h * 0.5f));

        Assert.True(screen.FurnaceButtonAt(centre, Width, Height));
        Assert.Equal(-1, screen.FurnaceSlotAt(centre, Width, Height));
        Assert.Equal(-1, screen.RecipeAt(centre, Width, Height));
    }

    /// <summary>V přežití mřížka předmětů kliky nebere, i kdyby na ni hráč trefil.</summary>
    [Fact]
    public void V_prezivani_mrizka_predmetu_neexistuje()
    {
        InventoryScreen screen = Screen();
        screen.Creative = false;

        Assert.Equal(ItemRegistry.Nothing, screen.CreativeAt(FirstCreativeCell(), Width, Height));
    }

    /// <summary>Pec přebíjí mřížku předmětů: u pece hráč řeší tavení.</summary>
    [Fact]
    public void Pec_prebiji_mrizku_predmetu()
    {
        InventoryScreen screen = Screen();
        screen.Creative = true;
        screen.Furnace = new Furnace();

        Assert.Equal(ItemRegistry.Nothing, screen.CreativeAt(FirstCreativeCell(), Width, Height));
    }

    /// <summary>
    /// Mřížka předmětů se vejde do okna.
    /// </summary>
    /// <remarks>
    /// Přetékala pod spodní hranu a poslední řádky visely na trávě. Počet řádků se proto
    /// počítá z místa, které v okně opravdu je — tenhle test hlídá, že se ten výpočet
    /// nerozejde s rámem okna.
    /// </remarks>
    [Theory]
    [InlineData(1280, 720)]
    [InlineData(1920, 1080)]
    [InlineData(2560, 1440)]
    [InlineData(800, 600)]
    public void Mrizka_predmetu_se_vejde_do_okna(int width, int height)
    {
        InventoryScreen screen = Screen();
        screen.Creative = true;

        (float wx, float wy, float ww, float wh) = InventoryScreen.Window(width, height);

        // Nejnižší a nejpravější bod, na který mřížka odpoví, musí ležet uvnitř okna.
        for (float y = wy; y < wy + (wh * 3f); y += 2f)
        {
            var probe = new Vector2(wx + ww - 4f, y);

            if (screen.CreativeAt(probe, width, height) != ItemRegistry.Nothing)
            {
                Assert.True(
                    y <= wy + wh,
                    $"Mřížka odpovídá na y={y}, ale okno končí na {wy + wh} — leze pod rám.");
            }
        }
    }

    /// <summary>Rolování posune mřížku o celé řádky, ne o kus.</summary>
    [Fact]
    public void Rolovani_posune_mrizku_o_radek()
    {
        InventoryScreen screen = Screen();
        screen.Creative = true;

        Vector2 cell = FirstCreativeCell();

        int first = screen.CreativeAt(cell, Width, Height);

        screen.CreativeScroll = 1;
        int second = screen.CreativeAt(cell, Width, Height);

        // Pět sloupců, takže o řádek níž je předmět o pět dál.
        Assert.Equal(screen.CreativeItemAt(0), first);
        Assert.Equal(screen.CreativeItemAt(5), second);
    }

    /// <summary>
    /// Rolování se dá dojet na konec a poslední předmět zůstane dosažitelný.
    /// </summary>
    /// <remarks>
    /// Kdyby strop rolování počítal s jiným počtem řádků než kreslení, byla by poslední
    /// hrstka předmětů v kreativu nedostupná — a nešlo by to poznat jinak než tím, že by
    /// je někdo hledal.
    /// </remarks>
    [Fact]
    public void Na_konci_rolovani_je_posledni_predmet_videt()
    {
        ItemRegistry items = Items();
        var screen = new InventoryScreen(items) { Open = true, Creative = true };

        screen.CreativeScroll = screen.CreativeMaxScroll(Width, Height);

        int expectedLast = Enumerable.Range(0, items.Count)
            .OrderBy(item => items.Definition(item).Id.StartsWith("tesseris:", StringComparison.Ordinal))
            .ThenBy(item => items.Definition(item).Id, StringComparer.Ordinal)
            .Last();
        bool found = false;

        (float x, float y, float w, float h) = InventoryScreen.Window(Width, Height);

        for (float py = y; py <= y + h && !found; py += 3f)
        {
            for (float px = x; px <= x + w && !found; px += 3f)
            {
                found = screen.CreativeAt(new Vector2(px, py), Width, Height) == expectedLast;
            }
        }

        Assert.True(found, "Poslední předmět v kreativu se nedá dorolovat.");
    }

    /// <summary>Klik mimo okno nesmí trefit slot — jinak by se nesené nedalo vyhodit.</summary>
    [Fact]
    public void Klik_mimo_okno_netrefi_nic()
    {
        InventoryScreen screen = Screen();
        screen.Creative = true;

        var far = new Vector2(5f, 5f);

        Assert.Equal(-1, screen.SlotAt(far, Width, Height));
        Assert.Equal(-1, screen.RecipeAt(far, Width, Height));
        Assert.Equal(ItemRegistry.Nothing, screen.CreativeAt(far, Width, Height));
    }

    /// <summary>
    /// Sloty výstroje se dají trefit a nepletou se s batohem.
    /// </summary>
    /// <remarks>
    /// Leží ve vlastním sloupci vlevo od batohu. Kdyby se překrývaly, přesunul by hráč
    /// místo přilby první slot batohu — a hledal by to v přesouvání, ne v rozvržení.
    /// </remarks>
    [Fact]
    public void Sloty_vystroje_jsou_pod_kurzorem_samy()
    {
        InventoryScreen screen = Screen();

        float size = InventoryScreen.SlotSize(Height);

        for (int index = 0; index < Inventory.ArmourSlots; index++)
        {
            Vector2 corner = screen.ArmourCorner(index, Width, Height);
            var centre = new Vector2(corner.X + (size * 0.5f), corner.Y + (size * 0.5f));

            Assert.Equal(index, screen.ArmourAt(centre, Width, Height));
            Assert.Equal(-1, screen.SlotAt(centre, Width, Height));
            Assert.Equal(-1, screen.RecipeAt(centre, Width, Height));
        }
    }

    /// <summary>
    /// Sloty výstroje jsou zarovnané na řádky mřížky.
    /// </summary>
    /// <remarks>
    /// Měly vlastní rozteč, která se s batohem rozešla, a čtvrtý slot skončil někde mezi
    /// řádky. Čtyři kusy výstroje a čtyři řádky panelu sedí na sebe přesně.
    /// </remarks>
    [Fact]
    public void Vystroj_je_zarovnana_na_radky_mrizky()
    {
        InventoryScreen screen = Screen();

        // Batoh má tři řádky, pás je čtvrtý.
        int[] rows = [Inventory.HotbarSlots, Inventory.HotbarSlots * 2, Inventory.HotbarSlots * 3, 0];

        for (int index = 0; index < Inventory.ArmourSlots; index++)
        {
            float expected = screen.SlotCorner(rows[index], Width, Height).Y;

            Assert.Equal(expected, screen.ArmourCorner(index, Width, Height).Y, 2);
        }
    }

    /// <summary>
    /// Silueta postavy má vlastní pruh a sloty do něj nesahají.
    /// </summary>
    /// <remarks>
    /// Kreslila se pod sloty — jenže slot je skoro neprůhledný, takže z panáka koukaly jen
    /// okraje a vypadal jako šmouha mezi sloupci. Slot proto musí začínat až za pruhem,
    /// který siluetě patří.
    /// </remarks>
    [Fact]
    public void Silueta_ma_vlastni_pruh()
    {
        InventoryScreen screen = Screen();

        (float wx, _, _, _) = InventoryScreen.Window(Width, Height);
        float size = InventoryScreen.SlotSize(Height);

        for (int index = 0; index < Inventory.ArmourSlots; index++)
        {
            float left = screen.ArmourCorner(index, Width, Height).X;

            Assert.True(
                left > wx + size,
                $"Slot výstroje {index} začíná na {left}, tedy ještě v pruhu siluety.");
        }
    }

    /// <summary>Receptář mapuje všechny recepty do pěti sloupců čistých ikon.</summary>
    [Theory]
    [InlineData(1280, 720)]
    [InlineData(1920, 1080)]
    [InlineData(800, 600)]
    public void Receptar_je_ikonova_mrizka(int width, int height)
    {
        ItemRegistry items = Items();
        RecipeBook recipes = RecipeBook.Load(
            items, Path.Combine(AppContext.BaseDirectory, "assets", "recipes"));
        var screen = new InventoryScreen(items) { Open = true, BookOpen = true };

        float size = InventoryScreen.SlotSize(height);
        (float x, float y, float w, _) = InventoryScreen.Window(width, height);
        float left = x + w - (size * 5.2f);
        float top = y + (size * 1.9f);
        float cell = (size * 4.9f) / InventoryScreen.BookColumns;

        Vector2 first = new(left + (cell * 0.5f), top + (cell * 0.5f));
        Vector2 lastInRow = new(
            left + ((InventoryScreen.BookColumns - 0.5f) * cell),
            top + (cell * 0.5f));
        Vector2 firstInSecondRow = new(left + (cell * 0.5f), top + (cell * 1.5f));

        Assert.Equal(0, screen.BookRowAt(first, width, height));
        Assert.Equal(InventoryScreen.BookColumns - 1, screen.BookRowAt(lastInRow, width, height));
        Assert.Equal(InventoryScreen.BookColumns, screen.BookRowAt(firstInSecondRow, width, height));
        Assert.Equal(-1, screen.BookRowAt(new Vector2(left - 2f, top), width, height));

        int maxScroll = screen.BookMaxScroll(recipes.Crafting.Count, width, height);
        screen.BookScroll = maxScroll;

        Assert.Equal(maxScroll * InventoryScreen.BookColumns, screen.BookRowAt(first, width, height));
    }

    /// <summary>
    /// Tooltip receptu drží ikonky surovin na společném pozadí s jejich textem.
    /// </summary>
    /// <remarks>
    /// V hlavní mřížce je jen ikona výsledku. Název a potřebné suroviny se objeví až po
    /// najetí myší; test hlídá počet ikon i společné rozvržení tooltipu.
    /// </remarks>
    [Theory]
    [InlineData(1280, 720)]
    [InlineData(1920, 1080)]
    [InlineData(800, 600)]
    public void Tooltip_receptu_obsahuje_ikony_surovin(int width, int height)
    {
        ItemRegistry items = Items();
        RecipeBook recipes = RecipeBook.Load(
            items, Path.Combine(AppContext.BaseDirectory, "assets", "recipes"));
        var inventory = new Inventory(items);
        var screen = new InventoryScreen(items) { Open = true, BookOpen = true };

        float size = InventoryScreen.SlotSize(height);
        (float x, float y, float w, _) = InventoryScreen.Window(width, height);
        float left = x + w - (size * 5.2f);
        float top = y + (size * 1.9f);
        float cell = (size * 4.9f) / InventoryScreen.BookColumns;

        screen.Pointer = new Vector2(left + (cell * 0.5f), top + (cell * 0.5f));

        Assert.Equal(0, screen.BookRowAt(screen.Pointer, width, height));

        (int lines, int icons, bool inside, bool clears) =
            screen.TooltipLayoutForTest(inventory, recipes, width, height);

        Assert.Equal(recipes.Crafting[0].Inputs.Length + 1, lines);
        Assert.Equal(recipes.Crafting[0].Inputs.Length, icons);
        Assert.True(inside, "Některá ikona suroviny leží mimo pozadí tooltipu.");
        Assert.True(clears, "Text suroviny začíná pod její ikonou.");
    }

    /// <summary>Celý sloupec výstroje se vejde do okna.</summary>
    [Theory]
    [InlineData(1280, 720)]
    [InlineData(1920, 1080)]
    [InlineData(800, 600)]
    public void Vystroj_se_vejde_do_okna(int width, int height)
    {
        InventoryScreen screen = Screen();

        float size = InventoryScreen.SlotSize(height);
        (float wx, float wy, float ww, float wh) = InventoryScreen.Window(width, height);

        for (int index = 0; index < Inventory.ArmourSlots; index++)
        {
            Vector2 corner = screen.ArmourCorner(index, width, height);

            Assert.True(corner.X >= wx && corner.X + size <= wx + ww, $"Slot výstroje {index} leze ven vodorovně.");
            Assert.True(corner.Y >= wy && corner.Y + size <= wy + wh, $"Slot výstroje {index} leze ven svisle.");
        }
    }

    /// <summary>
    /// Výroba ukazuje jen recepty, na které hráč má.
    /// </summary>
    /// <remarks>
    /// S prázdným inventářem tedy nic. Dřív to byl seznam všech receptů obarvený podle
    /// dostupnosti a hráč se prokousával deseti řádky „nemáš".
    /// </remarks>
    [Fact]
    public void Vyroba_ukazuje_jen_dostupne()
    {
        ItemRegistry items = Items();
        var inventory = new Inventory(items);

        RecipeBook recipes = RecipeBook.Load(
            items, Path.Combine(AppContext.BaseDirectory, "assets", "recipes"));

        Assert.Empty(InventoryScreen.Available(inventory, recipes));

        // Pět klacků stačí aspoň na jeden recept.
        inventory.Add(new ItemStack(items.IndexOf("tesseris:oak_log"), 8, 0));

        Assert.NotEmpty(InventoryScreen.Available(inventory, recipes));
    }

    /// <summary>Tlačítko knihy se dá trefit a nepřekrývá se s mřížkou výroby.</summary>
    [Fact]
    public void Tlacitko_knihy_je_tam_kde_se_kresli()
    {
        InventoryScreen screen = Screen();

        (float x, float y, float w, float h) = screen.BookButton(Width, Height);
        var centre = new Vector2(x + (w * 0.5f), y + (h * 0.5f));

        Assert.True(screen.BookButtonAt(centre, Width, Height));
        Assert.Equal(-1, screen.RecipeAt(centre, Width, Height));
        Assert.Equal(-1, screen.SlotAt(centre, Width, Height));
    }

    [Fact]
    public void Tlacitko_knihy_nelezi_v_kreativni_mrizce_a_v_kreativu_neni_aktivni()
    {
        InventoryScreen screen = Screen();
        (float x, float y, float w, float h) = screen.BookButton(Width, Height);
        var centre = new Vector2(x + (w * 0.5f), y + (h * 0.5f));

        screen.Creative = true;

        Assert.False(screen.BookButtonAt(centre, Width, Height));
        Assert.Equal(ItemRegistry.Nothing, screen.CreativeAt(centre, Width, Height));
        Assert.NotEqual(ItemRegistry.Nothing, screen.CreativeAt(FirstCreativeCell(), Width, Height));
    }

    /// <summary>V otevřené knize se nedá omylem vyrobit klikem do mřížky.</summary>
    [Fact]
    public void Otevrena_kniha_vypne_mrizku_vyroby()
    {
        InventoryScreen screen = Screen();
        screen.BookOpen = true;

        (float x, float y, float w, float h) = InventoryScreen.Window(Width, Height);
        float size = InventoryScreen.SlotSize(Height);

        var inGrid = new Vector2(x + w - (size * 4f), y + (size * 3f));

        Assert.Equal(-1, screen.RecipeAt(inGrid, Width, Height));
    }

    /// <summary>
    /// Do slotu výstroje se vejde jen kus, který tam patří.
    /// </summary>
    /// <remarks>
    /// Bez téhle kontroly by si hráč nasadil na hlavu kámen — a pak by nechápal, proč mu
    /// to nic nedělá. Vytáhnout se dá vždycky, tedy prázdná ruka projde do každého slotu.
    /// </remarks>
    [Fact]
    public void Do_slotu_vystroje_patri_jen_svuj_kus()
    {
        ItemRegistry items = Items();
        var inventory = new Inventory(items);

        var helmet = new ItemStack(items.IndexOf("tesseris:iron_helmet"), 1, 0);
        var boots = new ItemStack(items.IndexOf("tesseris:diamond_boots"), 1, 0);
        var stone = new ItemStack(items.IndexOf("tesseris:stone"), 1, 0);

        Assert.True(inventory.FitsArmourSlot(helmet, (int)ArmourSlot.Helmet));
        Assert.False(inventory.FitsArmourSlot(helmet, (int)ArmourSlot.Boots));

        Assert.True(inventory.FitsArmourSlot(boots, (int)ArmourSlot.Boots));
        Assert.False(inventory.FitsArmourSlot(boots, (int)ArmourSlot.Chestplate));

        // Kámen nepatří nikam, prázdná ruka všude.
        for (int slot = 0; slot < Inventory.ArmourSlots; slot++)
        {
            Assert.False(inventory.FitsArmourSlot(stone, slot));
            Assert.True(inventory.FitsArmourSlot(ItemStack.Empty, slot));
        }
    }

    /// <summary>
    /// Ochrana se sčítá a má strop.
    /// </summary>
    /// <remarks>
    /// Plná nezranitelnost by z brnění udělala vypínač obtížnosti, proto strop pod
    /// jedničkou. Diamantová sada ho má překročit, aby bylo poznat, že se opravdu uplatní.
    /// </remarks>
    [Fact]
    public void Ochrana_se_scita_a_ma_strop()
    {
        ItemRegistry items = Items();
        var inventory = new Inventory(items);

        Assert.Equal(0f, inventory.Protection, 3);

        inventory[Inventory.FirstArmourSlot + (int)ArmourSlot.Helmet] =
            new ItemStack(items.IndexOf("tesseris:iron_helmet"), 1, 0);

        Assert.True(inventory.Protection > 0f, "Nasazená přilba nic nechrání.");

        foreach ((ArmourSlot slot, string piece) in new[]
        {
            (ArmourSlot.Helmet, "helmet"), (ArmourSlot.Chestplate, "chestplate"),
            (ArmourSlot.Leggings, "leggings"), (ArmourSlot.Boots, "boots"),
        })
        {
            inventory[Inventory.FirstArmourSlot + (int)slot] =
                new ItemStack(items.IndexOf($"tesseris:diamond_{piece}"), 1, 0);
        }

        Assert.Equal(Inventory.MaxProtection, inventory.Protection, 3);
    }

    /// <summary>Výstroj se nepočítá do surovin na výrobu.</summary>
    /// <remarks>
    /// Kdyby ano, hlásila by výroba, že na recept je — a pak by ho neuměla vyrobit, protože
    /// odebírání sahá jen do pásu a batohu. Hráč by viděl recept, který jde označit a nic
    /// se nestane.
    /// </remarks>
    [Fact]
    public void Vystroj_se_nepocita_do_surovin()
    {
        ItemRegistry items = Items();
        var inventory = new Inventory(items);

        int helmet = items.IndexOf("tesseris:iron_helmet");

        inventory[Inventory.FirstArmourSlot] = new ItemStack(helmet, 1, 0);

        Assert.Equal(0, inventory.CountOf(helmet));
    }

    /// <summary>Sloty inventáře se navzájem nepřekrývají a každý je pod kurzorem sám.</summary>
    [Fact]
    public void Kazdy_slot_inventare_je_pod_kurzorem_sam()
    {
        InventoryScreen screen = Screen();

        float size = InventoryScreen.SlotSize(Height);

        for (int slot = 0; slot < Inventory.TotalSlots; slot++)
        {
            Vector2 corner = screen.SlotCorner(slot, Width, Height);
            var centre = new Vector2(corner.X + (size * 0.5f), corner.Y + (size * 0.5f));

            Assert.Equal(slot, screen.SlotAt(centre, Width, Height));
        }
    }
}
