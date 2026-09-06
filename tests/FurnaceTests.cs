using OpenTK.Mathematics;
using Tesseris.Game.Blocks;
using Tesseris.Game.Items;
using Tesseris.ModApi;
using Xunit;

namespace Tesseris.Tests;

/// <summary>
/// Tavení v peci.
///
/// <para>Je to stavový automat s časem a palivem, takže se v něm dá snadno ztratit
/// surovina nebo naopak vyrobit něco z ničeho. Obojí se ve hře pozná pozdě a hlásí se
/// špatně, proto na to testy.</para>
///
/// <para>Rozvržení je jako v Rustu: nahoře jeden slot na palivo, uprostřed dva na suroviny,
/// dole tři na výstup — a <b>pec se musí zapnout</b>.</para>
/// </summary>
public sealed class FurnaceTests
{
    private static (ItemRegistry Items, Furnaces Furnaces) Setup()
    {
        BlockRegistry blocks = BlockRegistry.LoadFromDirectory(
            Path.Combine(AppContext.BaseDirectory, "assets", "blocks"));

        ItemRegistry items = ItemRegistry.Create(
            blocks, Path.Combine(AppContext.BaseDirectory, "assets", "items"));

        RecipeBook recipes = RecipeBook.Load(
            items, Path.Combine(AppContext.BaseDirectory, "assets", "recipes"));

        return (items, new Furnaces(items, recipes));
    }

    /// <summary>Pec s železem v prvním vstupu a uhlím v palivu, zapnutá.</summary>
    private static Furnace Loaded(ItemRegistry items, Furnaces furnaces, int rawIron = 1, int coal = 1)
    {
        Furnace furnace = furnaces.At(Vector3i.Zero);

        if (rawIron > 0)
        {
            furnace.Input[0] = new ItemStack(items.IndexOf("tesseris:raw_iron"), rawIron, 0);
        }

        if (coal > 0)
        {
            furnace.Fuel = new ItemStack(items.IndexOf("tesseris:coal"), coal, 0);
        }

        furnace.Running = true;
        return furnace;
    }

    /// <summary>Kolik kusů daného předmětu leží ve výstupu dohromady.</summary>
    private static int InOutput(ItemRegistry items, Furnace furnace, string id)
    {
        int item = items.IndexOf(id);
        int total = 0;

        foreach (ItemStack stack in furnace.Output)
        {
            if (!stack.IsEmpty && stack.Item == item)
            {
                total += stack.Count;
            }
        }

        return total;
    }

    [Fact]
    public void Surove_zelezo_se_prataví_na_ingot()
    {
        (ItemRegistry items, Furnaces furnaces) = Setup();
        Furnace furnace = Loaded(items, furnaces);

        // O chlup víc, než trvá jedno tavení.
        furnaces.Update(Furnaces.SmeltSeconds + 0.2f);

        Assert.Equal(1, InOutput(items, furnace, "tesseris:iron_ingot"));
        Assert.True(furnace.Input[0].IsEmpty);
    }

    /// <summary>
    /// Vypnutá pec nedělá nic, i když má všechno.
    /// </summary>
    /// <remarks>
    /// <b>Tohle je celý smysl vypínače.</b> Bez něj by pec spálila palivo hned, jak by do ní
    /// hráč něco dal, takže by nešla nechat naloženou na později ani použít jako bednu.
    /// </remarks>
    [Fact]
    public void Vypnuta_pec_netavi_ani_netopi()
    {
        (ItemRegistry items, Furnaces furnaces) = Setup();
        Furnace furnace = Loaded(items, furnaces, rawIron: 3, coal: 3);

        furnace.Running = false;

        furnaces.Update(Furnaces.SmeltSeconds * 5f);

        Assert.Equal(3, furnace.Input[0].Count);
        Assert.Equal(3, furnace.Fuel.Count);
        Assert.False(furnace.Burning);
        Assert.Equal(0, InOutput(items, furnace, "tesseris:iron_ingot"));
    }

    /// <summary>
    /// Bez paliva se netaví a pec zhasne.
    /// </summary>
    /// <remarks>
    /// Zhasnutí je podstatné: kdyby zůstala zapnutá, spálila by první dřevo, které do ní hráč
    /// později dá, aniž by o to stál.
    /// </remarks>
    [Fact]
    public void Bez_paliva_pec_zhasne()
    {
        (ItemRegistry items, Furnaces furnaces) = Setup();
        Furnace furnace = Loaded(items, furnaces, coal: 0);

        furnaces.Update(Furnaces.SmeltSeconds * 3f);

        Assert.Equal(0, InOutput(items, furnace, "tesseris:iron_ingot"));
        Assert.Equal(1, furnace.Input[0].Count);
        Assert.False(furnace.Running);
    }

    /// <summary>
    /// Ze dřeva v palivu vzniká dřevěné uhlí.
    /// </summary>
    /// <remarks>
    /// <para>Není to tavení a nemá to recept: hráč přiloží dřevo a uhel je vedlejší produkt
    /// topení. Proto stačí dát dřevo do horního slotu a pec zapnout — surovina uprostřed
    /// být nemusí.</para>
    /// </remarks>
    [Fact]
    public void Ze_dreva_vznika_drevene_uhli()
    {
        (ItemRegistry items, Furnaces furnaces) = Setup();
        Furnace furnace = furnaces.At(Vector3i.Zero);

        furnace.Fuel = new ItemStack(items.IndexOf("tesseris:oak_log"), 2, 0);
        furnace.Running = true;

        // Jeden kus se zapálí hned a uhel padne rovnou; druhý až po dohoření prvního.
        furnaces.Update(0.1f);

        Assert.Equal(1, InOutput(items, furnace, "tesseris:charcoal"));
        Assert.Equal(1, furnace.Fuel.Count);
        Assert.True(furnace.Burning);
    }

    /// <summary>Uhlí uhlí nedělá, jinak by pec vyráběla palivo z paliva donekonečna.</summary>
    [Fact]
    public void Z_uhli_dalsi_uhli_nevznika()
    {
        (ItemRegistry items, Furnaces furnaces) = Setup();
        Furnace furnace = Loaded(items, furnaces, rawIron: 0, coal: 2);

        furnaces.Update(0.1f);

        Assert.Equal(0, InOutput(items, furnace, "tesseris:charcoal"));
        Assert.Equal(0, InOutput(items, furnace, "tesseris:coal"));
    }

    /// <summary>Dřevěné uhlí se dá zase spálit, jinak by nebylo k ničemu.</summary>
    [Fact]
    public void Drevene_uhli_hori()
    {
        (ItemRegistry items, Furnaces furnaces) = Setup();

        Assert.True(furnaces.IsFuel(new ItemStack(items.IndexOf("tesseris:charcoal"), 1, 0)));
    }

    /// <summary>Dřevo je palivo — klády i prkna.</summary>
    [Theory]
    [InlineData("tesseris:oak_log")]
    [InlineData("tesseris:spruce_log")]
    [InlineData("tesseris:birch_log")]
    [InlineData("tesseris:acacia_log")]
    [InlineData("tesseris:maple_log")]
    [InlineData("tesseris:planks")]
    public void Drevo_je_palivo(string id)
    {
        (ItemRegistry items, Furnaces furnaces) = Setup();

        int item = items.IndexOf(id);

        Assert.NotEqual(ItemRegistry.Nothing, item);
        Assert.True(furnaces.IsFuel(new ItemStack(item, 1, 0)), $"'{id}' nehoří.");
        Assert.Equal("tesseris:charcoal", items.Definition(item).BurnResidue);
    }

    /// <summary>
    /// Bez místa na uhel se dřevo nezapálí.
    /// </summary>
    /// <remarks>
    /// Kdyby shořelo a uhel se zahodil, přišel by hráč o dřevo i o uhlí, aniž by se cokoli
    /// stalo — a vypadalo by to, že se palivo ztrácí samo.
    /// </remarks>
    [Fact]
    public void Bez_mista_na_uhel_drevo_neshori()
    {
        (ItemRegistry items, Furnaces furnaces) = Setup();
        Furnace furnace = furnaces.At(Vector3i.Zero);

        furnace.Fuel = new ItemStack(items.IndexOf("tesseris:oak_log"), 4, 0);
        furnace.Running = true;

        // Všechny tři výstupy zabrané něčím jiným a plné.
        int diamond = items.IndexOf("tesseris:diamond");
        int max = items.Definition(diamond).MaxStack;

        for (int slot = 0; slot < Furnace.OutputSlots; slot++)
        {
            furnace.Output[slot] = new ItemStack(diamond, max, 0);
        }

        furnaces.Update(1f);

        Assert.Equal(4, furnace.Fuel.Count);
        Assert.False(furnace.Burning);
    }

    /// <summary>Oba vstupní sloty se taví zároveň, ne jeden po druhém.</summary>
    [Fact]
    public void Oba_vstupy_se_tavi_zaroven()
    {
        (ItemRegistry items, Furnaces furnaces) = Setup();
        Furnace furnace = furnaces.At(Vector3i.Zero);

        furnace.Input[0] = new ItemStack(items.IndexOf("tesseris:raw_iron"), 2, 0);
        furnace.Input[1] = new ItemStack(items.IndexOf("tesseris:sand"), 2, 0);
        furnace.Fuel = new ItemStack(items.IndexOf("tesseris:coal"), 1, 0);
        furnace.Running = true;

        furnaces.Update(Furnaces.SmeltSeconds + 0.2f);

        Assert.Equal(1, InOutput(items, furnace, "tesseris:iron_ingot"));
        Assert.Equal(1, InOutput(items, furnace, "tesseris:glass"));
    }

    /// <summary>
    /// Jedno uhlí vydrží víc tavení. Kdyby se přikládalo na každý kus zvlášť, bylo by
    /// palivo osmkrát dražší, než má být.
    /// </summary>
    [Fact]
    public void Jedno_uhli_stihne_vic_taveni()
    {
        (ItemRegistry items, Furnaces furnaces) = Setup();
        Furnace furnace = Loaded(items, furnaces, rawIron: 4, coal: 1);

        // Čtyři tavení po deseti vteřinách, s rezervou: sčítání postupu po desetinách skončí
        // o zlomek pod jedničkou. Uhlí hoří osmdesát vteřin, rezerva se do něj vejde.
        for (int i = 0; i < 430; i++)
        {
            furnaces.Update(0.1f);
        }

        Assert.Equal(4, InOutput(items, furnace, "tesseris:iron_ingot"));
        Assert.True(furnace.Fuel.IsEmpty, "Uhlí se mělo přiložit právě jednou.");
    }

    /// <summary>Do plného výstupu se tavit nesmí, jinak by se surovina ztratila.</summary>
    [Fact]
    public void Plny_vystup_zastavi_taveni()
    {
        (ItemRegistry items, Furnaces furnaces) = Setup();
        Furnace furnace = Loaded(items, furnaces, rawIron: 5, coal: 5);

        int ingot = items.IndexOf("tesseris:iron_ingot");
        int max = items.Definition(ingot).MaxStack;

        for (int slot = 0; slot < Furnace.OutputSlots; slot++)
        {
            furnace.Output[slot] = new ItemStack(ingot, max, 0);
        }

        furnaces.Update(Furnaces.SmeltSeconds * 2f);

        Assert.Equal(5, furnace.Input[0].Count);
        Assert.Equal(max * Furnace.OutputSlots, InOutput(items, furnace, "tesseris:iron_ingot"));
    }

    /// <summary>
    /// Výsledek dorovná rozdělanou hromádku, než zabere prázdný slot.
    /// </summary>
    /// <remarks>
    /// Kdyby se bral první volný slot, rozpadlo by se dvacet ingotů na tři hromádky po pár
    /// kusech a výstup by hlásil „plno", i když se do něj vejde ještě spousta.
    /// </remarks>
    [Fact]
    public void Vysledek_dorovnava_rozdelanou_hromadku()
    {
        (ItemRegistry items, Furnaces furnaces) = Setup();
        Furnace furnace = Loaded(items, furnaces, rawIron: 1, coal: 1);

        int ingot = items.IndexOf("tesseris:iron_ingot");
        furnace.Output[0] = new ItemStack(ingot, 5, 0);

        furnaces.Update(Furnaces.SmeltSeconds + 0.2f);

        Assert.Equal(6, furnace.Output[0].Count);
        Assert.True(furnace.Output[1].IsEmpty, "Ingot si otevřel nový slot, i když měl kam.");
    }

    /// <summary>
    /// Ruda se dá tavit i jako blok, ne jen jako surovina z ní.
    /// </summary>
    /// <remarks>
    /// Hlášeno jako „ten iron mi do inputu nejde dát": v kreativu si hráč vezme z mřížky
    /// <b>blok rudy</b>, ne surovinu, kterou z rudy vytěží. Recept znal jen surovinu, takže
    /// pec blok odmítla — a hráč z toho viděl jen to, že se předmět nedá položit.
    /// </remarks>
    [Theory]
    [InlineData("tesseris:iron_ore", "tesseris:iron_ingot")]
    [InlineData("tesseris:coal_ore", "tesseris:coal")]
    [InlineData("tesseris:diamond_ore", "tesseris:diamond")]
    [InlineData("tesseris:raw_iron", "tesseris:iron_ingot")]
    [InlineData("tesseris:sand", "tesseris:glass")]
    public void Ruda_i_surovina_se_tavi(string input, string output)
    {
        (ItemRegistry items, Furnaces furnaces) = Setup();

        int item = items.IndexOf(input);
        Assert.NotEqual(ItemRegistry.Nothing, item);

        RecipeBook.Entry? recipe = furnaces.FindRecipe(new ItemStack(item, 1, 0));

        Assert.True(recipe.HasValue, $"'{input}' nemá recept na tavení.");
        Assert.Equal(items.IndexOf(output), recipe!.Value.Output);
    }

    [Fact]
    public void Dlazebni_kostka_se_v_peci_neprepaluje()
    {
        (ItemRegistry items, Furnaces furnaces) = Setup();
        int cobblestone = items.IndexOf("tesseris:cobblestone");

        RecipeBook.Entry? recipe = furnaces.FindRecipe(new ItemStack(cobblestone, 1, 0));

        Assert.False(recipe.HasValue);
    }

    /// <summary>Blok rudy v peci opravdu skončí jako ingot, ne jen že má recept.</summary>
    [Fact]
    public void Blok_zelezne_rudy_se_prataví()
    {
        (ItemRegistry items, Furnaces furnaces) = Setup();
        Furnace furnace = furnaces.At(Vector3i.Zero);

        furnace.Input[0] = new ItemStack(items.IndexOf("tesseris:iron_ore"), 1, 0);
        furnace.Fuel = new ItemStack(items.IndexOf("tesseris:coal"), 1, 0);
        furnace.Running = true;

        furnaces.Update(Furnaces.SmeltSeconds + 0.2f);

        Assert.Equal(1, InOutput(items, furnace, "tesseris:iron_ingot"));
    }

    [Fact]
    public void Nastroj_neni_palivo_ani_surovina()
    {
        (ItemRegistry items, Furnaces furnaces) = Setup();

        var pickaxe = new ItemStack(items.IndexOf("tesseris:iron_pickaxe"), 1, 0);

        Assert.False(furnaces.IsFuel(pickaxe));
        Assert.Null(furnaces.FindRecipe(pickaxe));
    }

    [Fact]
    public void Uhli_i_klacky_hori()
    {
        (ItemRegistry items, Furnaces furnaces) = Setup();

        Assert.True(furnaces.IsFuel(new ItemStack(items.IndexOf("tesseris:coal"), 1, 0)));
        Assert.True(furnaces.IsFuel(new ItemStack(items.IndexOf("tesseris:stick"), 1, 0)));
    }

    /// <summary>Rozbitá pec vysype všechno, co v ní bylo — ze všech šesti slotů.</summary>
    [Fact]
    public void Rozbita_pec_vysype_obsah()
    {
        (ItemRegistry items, Furnaces furnaces) = Setup();
        Furnace furnace = Loaded(items, furnaces, rawIron: 2, coal: 3);

        furnace.Input[1] = new ItemStack(items.IndexOf("tesseris:cobblestone"), 7, 0);
        furnace.Output[2] = new ItemStack(items.IndexOf("tesseris:iron_ingot"), 1, 0);

        List<ItemStack> spilled = [.. furnaces.Remove(Vector3i.Zero).Where(s => !s.IsEmpty)];

        Assert.Equal(4, spilled.Count);
        Assert.Contains(spilled, s => s.Count == 2);
        Assert.Contains(spilled, s => s.Count == 3);
        Assert.Contains(spilled, s => s.Count == 7);
        Assert.Equal(0, furnaces.Count);
    }

    /// <summary>Dočasná složka, která po sobě uklidí.</summary>
    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tesseris-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string File => System.IO.Path.Combine(Path, "pece.dat");

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // Úklid dočasné složky není podstatný pro výsledek testu.
            }
        }
    }

    /// <summary>
    /// Obsah pece přežije restart, včetně toho, jestli byla zapnutá.
    /// </summary>
    /// <remarks>
    /// Ukládá se vedle světa, ne do chunku — formát regionu umí bloky, ne hromádky předmětů
    /// s časovači. Test hlídá celý kruh: zapsat, načíst novým registrem, porovnat.
    /// </remarks>
    [Fact]
    public void Obsah_pece_prezije_ulozeni_a_nacteni()
    {
        using var temp = new TempDirectory();

        (ItemRegistry items, Furnaces furnaces) = Setup();
        Furnace before = Loaded(items, furnaces, rawIron: 3, coal: 2);

        before.Input[1] = new ItemStack(items.IndexOf("tesseris:cobblestone"), 9, 0);
        before.Output[1] = new ItemStack(items.IndexOf("tesseris:iron_ingot"), 5, 0);
        before.BurnLeft = 42.5f;
        before.BurnTotal = 80f;
        before.Progress[0] = 0.25f;

        furnaces.Save(temp.File);

        // Druhý běh hry: čerstvé pece, tentýž svět.
        (ItemRegistry restoredItems, Furnaces restored) = Setup();

        Assert.Equal(1, restored.Load(temp.File));

        Furnace after = restored.At(Vector3i.Zero);

        Assert.Equal(restoredItems.IndexOf("tesseris:coal"), after.Fuel.Item);
        Assert.Equal(2, after.Fuel.Count);
        Assert.Equal(restoredItems.IndexOf("tesseris:raw_iron"), after.Input[0].Item);
        Assert.Equal(3, after.Input[0].Count);
        Assert.Equal(9, after.Input[1].Count);
        Assert.Equal(restoredItems.IndexOf("tesseris:iron_ingot"), after.Output[1].Item);
        Assert.Equal(5, after.Output[1].Count);

        Assert.Equal(42.5f, after.BurnLeft, 3);
        Assert.Equal(80f, after.BurnTotal, 3);
        Assert.Equal(0.25f, after.Progress[0], 3);
        Assert.True(after.Running, "Zapnutá pec se po restartu probudila vypnutá.");
    }

    /// <summary>Opotřebení se ukládá taky, jinak by se pec dala použít jako opravna.</summary>
    [Fact]
    public void Ulozeni_zachova_opotrebeni()
    {
        using var temp = new TempDirectory();

        (ItemRegistry items, Furnaces furnaces) = Setup();
        Furnace furnace = furnaces.At(Vector3i.Zero);

        furnace.Output[0] = new ItemStack(items.IndexOf("tesseris:iron_pickaxe"), 1, 37);

        furnaces.Save(temp.File);

        (_, Furnaces restored) = Setup();
        restored.Load(temp.File);

        Assert.Equal(37, restored.At(Vector3i.Zero).Output[0].Damage);
    }

    /// <summary>
    /// Prázdná pec se neukládá a soubor po ní nezůstane.
    /// </summary>
    /// <remarks>
    /// Záznam vzniká už tím, že hráč pec otevře. Kdyby se ukládaly i prázdné, rostl by
    /// soubor s každým kliknutím. A kdyby po vybrání poslední pece zůstal starý soubor
    /// ležet, obnovil by se z něj při dalším spuštění obsah, který hráč právě odnesl.
    /// </remarks>
    [Fact]
    public void Prazdna_pec_se_neuklada()
    {
        using var temp = new TempDirectory();

        (ItemRegistry items, Furnaces furnaces) = Setup();

        Furnace furnace = Loaded(items, furnaces);
        furnaces.Save(temp.File);
        Assert.True(File.Exists(temp.File));

        // Hráč pec vybral.
        furnace.Fuel = ItemStack.Empty;
        furnace.Input[0] = ItemStack.Empty;

        furnaces.Save(temp.File);

        Assert.False(File.Exists(temp.File), "Po vybrané peci zůstal soubor ležet.");
    }

    /// <summary>Chybějící soubor není chyba — první spuštění světa žádný nemá.</summary>
    [Fact]
    public void Chybejici_soubor_neni_chyba()
    {
        using var temp = new TempDirectory();

        (_, Furnaces furnaces) = Setup();

        Assert.Equal(0, furnaces.Load(temp.File));
        Assert.Equal(0, furnaces.Count);
    }

    /// <summary>
    /// Poškozený soubor hru neshodí.
    /// </summary>
    /// <remarks>
    /// Obsah pece je ozdoba, svět je to podstatné. Pád při načítání by hráče připravil
    /// o obojí.
    /// </remarks>
    [Fact]
    public void Poskozeny_soubor_hru_neshodi()
    {
        using var temp = new TempDirectory();

        File.WriteAllBytes(temp.File, [1, 2, 3, 4, 5, 6, 7, 8]);

        (_, Furnaces furnaces) = Setup();

        Assert.Equal(0, furnaces.Load(temp.File));
        Assert.Equal(0, furnaces.Count);
    }

    /// <summary>
    /// Předměty se ukládají jménem, ne indexem.
    /// </summary>
    /// <remarks>
    /// Registr předmětů vzniká z bloků seřazených abecedně, takže přidání jediného bloku
    /// posune indexy všech za ním. Kdyby se ukládal index, proměnilo by se po každém takovém
    /// přidání železo v peci na něco jiného — a nikdo by netušil proč.
    /// </remarks>
    [Fact]
    public void V_souboru_jsou_jmena_predmetu()
    {
        using var temp = new TempDirectory();

        (ItemRegistry items, Furnaces furnaces) = Setup();
        Loaded(items, furnaces);

        furnaces.Save(temp.File);

        string raw = System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(temp.File));

        Assert.Contains("tesseris:raw_iron", raw, StringComparison.Ordinal);
        Assert.Contains("tesseris:coal", raw, StringComparison.Ordinal);
    }

    [Fact]
    public void Stavove_komponenty_stacku_prežiji_ulozeni_pece()
    {
        using var temp = new TempDirectory();
        (ItemRegistry items, Furnaces furnaces) = Setup();
        var at = new Vector3i(7, 8, 9);
        Furnace furnace = furnaces.At(at);
        ItemComponentMap components = ItemComponentMap.Empty.Set(
            new ResourceId("test:energy"),
            new ModSerializedValue(new ResourceId("test:int32"), 2, new byte[] { 42, 0, 0, 0 }));
        furnace.Output[0] = new ItemStack(items.IndexOf("tesseris:iron_pickaxe"), 1, 17, components);

        furnaces.Save(temp.File);
        (_, Furnaces restored) = Setup();
        Assert.Equal(1, restored.Load(temp.File));

        Assert.Equal(furnace.Output[0], restored.At(at).Output[0]);
    }
}
