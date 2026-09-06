using Tesseris.Game.Blocks;
using Tesseris.Game.Items;
using Xunit;

namespace Tesseris.Tests;

/// <summary>
/// Inventář, těžba a opotřebení.
///
/// <para>Je to čistá logika bez grafiky, takže se dá testovat celá — a měla by být,
/// protože chyba v počítání kusů se pozná až tím, že hráči zmizí nebo přibude materiál,
/// a to se hlásí špatně.</para>
/// </summary>
public sealed class InventoryTests
{
    private static BlockRegistry Blocks() =>
        BlockRegistry.LoadFromDirectory(Path.Combine(AppContext.BaseDirectory, "assets", "blocks"));

    private static ItemRegistry Items(BlockRegistry blocks) =>
        ItemRegistry.Create(blocks, Path.Combine(AppContext.BaseDirectory, "assets", "items"));

    private static int Stone(ItemRegistry items) => items.IndexOf("tesseris:stone");

    [Fact]
    public void Bloky_se_promeni_na_predmety_samy()
    {
        BlockRegistry blocks = Blocks();
        ItemRegistry items = Items(blocks);

        Assert.NotEqual(ItemRegistry.Nothing, items.IndexOf("tesseris:stone"));
        Assert.NotEqual(ItemRegistry.Nothing, items.IndexOf("tesseris:oak_log"));

        // A převod tam a zpátky musí souhlasit, jinak by se vytěžil jiný blok, než co spadne.
        ushort stone = blocks.IndexOf("tesseris:stone");
        Assert.Equal(stone, items.BlockForItem(items.ItemForBlock(stone)));
    }

    /// <summary>
    /// Voda předmět nedostane. Kdyby ho dostala, dala by se nosit v dlani a stavět z ní.
    /// </summary>
    [Fact]
    public void Voda_predmet_nema()
    {
        BlockRegistry blocks = Blocks();
        ItemRegistry items = Items(blocks);

        Assert.Equal(ItemRegistry.Nothing, items.ItemForBlock(blocks.IndexOf("tesseris:water")));
    }

    [Fact]
    public void Nastroje_se_nestohuji()
    {
        BlockRegistry blocks = Blocks();
        ItemRegistry items = Items(blocks);

        int pickaxe = items.IndexOf("tesseris:iron_pickaxe");

        Assert.NotEqual(ItemRegistry.Nothing, pickaxe);
        Assert.Equal(1, items.Definition(pickaxe).MaxStack);
        Assert.True(items.Definition(pickaxe).Durability > 0);
    }

    /// <summary>
    /// Detailní UV textura modelu nesmí skončit v inventáři: slot musí dostat vlastní,
    /// čitelnou siluetu nástroje.
    /// </summary>
    [Fact]
    public void Modelove_textury_nastroju_maji_vlastni_ikony()
    {
        BlockRegistry blocks = Blocks();
        ItemRegistry items = Items(blocks);

        foreach (string id in new[]
        {
            "tesseris:flint_pickaxe", "tesseris:stone_pickaxe", "tesseris:diamond_pickaxe",
        })
        {
            ItemDefinition item = items.Definition(items.IndexOf(id));
            Assert.False(string.IsNullOrWhiteSpace(item.IconTexture));
            Assert.NotEqual(item.Texture, item.IconTexture);
            Assert.Contains(item.IconTexture, items.IconTextures());
        }
    }

    [Fact]
    public void Modelove_bloky_pouzivaji_vlastni_ikony_misto_uv_atlasu()
    {
        BlockRegistry blocks = Blocks();
        ItemRegistry items = Items(blocks);

        foreach (string id in new[]
        {
            "tesseris:furnace",
            "tesseris:alloy_smelter",
            "tesseris:battery_rack_large",
            "tesseris:battery_rack_small",
            "tesseris:border_torch",
            "tesseris:machine_frame",
            "tesseris:reinforced_machine_frame",
            "tesseris:ore_crusher",
            "tesseris:metal_press",
            "tesseris:quarry",
            "tesseris:solar_panel",
            "tesseris:wind_turbine",
        })
        {
            int index = items.IndexOf(id);
            ItemDefinition item = items.Definition(index);

            Assert.False(string.IsNullOrWhiteSpace(item.IconTexture));
            Assert.Contains(item.IconTexture, items.IconTextures());

            items.ResolveIcons(name => name == item.IconTexture ? 73 : 11);
            Assert.Equal(73, items.IconLayer(index));
        }
    }

    /// <summary>
    /// Nejdřív se dosypává do rozdělaných hromádek. Obráceně by se inventář zaplnil
    /// devíti sloty po jednom kameni, přestože by se všechny vešly do jednoho.
    /// </summary>
    [Fact]
    public void Pridavani_nejdriv_doplni_rozdelanou_hromadku()
    {
        BlockRegistry blocks = Blocks();
        ItemRegistry items = Items(blocks);
        var inventory = new Inventory(items);

        int stone = Stone(items);

        inventory[0] = new ItemStack(stone, 60, 0);
        inventory.Add(new ItemStack(stone, 3, 0));

        Assert.Equal(63, inventory[0].Count);
        Assert.True(inventory[1].IsEmpty);
    }

    [Fact]
    public void Pres_plnou_hromadku_se_prelije_do_dalsiho_slotu()
    {
        BlockRegistry blocks = Blocks();
        ItemRegistry items = Items(blocks);
        var inventory = new Inventory(items);

        int stone = Stone(items);

        ItemStack left = inventory.Add(new ItemStack(stone, 100, 0));

        Assert.True(left.IsEmpty);
        Assert.Equal(100, inventory.CountOf(stone));
        Assert.Equal(64, inventory[0].Count);
        Assert.Equal(36, inventory[1].Count);
    }

    [Fact]
    public void Co_se_nevejde_se_vrati()
    {
        BlockRegistry blocks = Blocks();
        ItemRegistry items = Items(blocks);
        var inventory = new Inventory(items);

        int stone = Stone(items);

        for (int slot = 0; slot < Inventory.TotalSlots; slot++)
        {
            inventory[slot] = new ItemStack(stone, 64, 0);
        }

        ItemStack left = inventory.Add(new ItemStack(stone, 10, 0));

        Assert.Equal(10, left.Count);
    }

    /// <summary>
    /// Odebrání je všechno nebo nic. Výroba se ptá na několik surovin a musí jít zrušit,
    /// když poslední z nich chybí — částečný odběr by hráči suroviny sežral.
    /// </summary>
    [Fact]
    public void Nedostatek_surovin_neodebere_nic()
    {
        BlockRegistry blocks = Blocks();
        ItemRegistry items = Items(blocks);
        var inventory = new Inventory(items);

        int stone = Stone(items);
        inventory.Add(new ItemStack(stone, 5, 0));

        Assert.False(inventory.Remove(stone, 9));
        Assert.Equal(5, inventory.CountOf(stone));

        Assert.True(inventory.Remove(stone, 5));
        Assert.Equal(0, inventory.CountOf(stone));
    }

    /// <summary>
    /// Dva nástroje s různým opotřebením se nesmí slít — jinak by se opotřebení jednoho
    /// z nich tiše ztratilo.
    /// </summary>
    [Fact]
    public void Ruzne_opotrebeni_se_nesleje()
    {
        var fresh = new ItemStack(7, 1, 0);
        var worn = new ItemStack(7, 1, 40);

        Assert.False(fresh.Matches(worn));
        Assert.True(fresh.Matches(fresh with { Count = 1 }));
    }

    [Fact]
    public void Nastroj_se_opotrebi_a_rozpadne()
    {
        BlockRegistry blocks = Blocks();
        ItemRegistry items = Items(blocks);
        var inventory = new Inventory(items);

        int pickaxe = items.IndexOf("tesseris:flint_pickaxe");
        int durability = items.Definition(pickaxe).Durability;

        inventory[0] = new ItemStack(pickaxe, 1, 0);

        for (int i = 0; i < durability - 1; i++)
        {
            Assert.False(inventory.DamageSelected(1), $"Krumpáč se rozpadl už po {i + 1} použitích.");
        }

        Assert.True(inventory.DamageSelected(1));
        Assert.True(inventory[0].IsEmpty);
    }

    /// <summary>Blok se nesmí opotřebovat. Opotřebení má jen to, co má durability.</summary>
    [Fact]
    public void Blok_se_neopotrebuje()
    {
        BlockRegistry blocks = Blocks();
        ItemRegistry items = Items(blocks);
        var inventory = new Inventory(items);

        inventory[0] = new ItemStack(Stone(items), 10, 0);

        Assert.False(inventory.DamageSelected(1));
        Assert.Equal(10, inventory[0].Count);
    }

    [Fact]
    public void Vyber_v_pasu_se_obtoci()
    {
        BlockRegistry blocks = Blocks();
        var inventory = new Inventory(Items(blocks));

        inventory.Select(0);
        inventory.Scroll(-1);

        Assert.Equal(Inventory.HotbarSlots - 1, inventory.Selected);

        inventory.Scroll(1);
        Assert.Equal(0, inventory.Selected);
    }

    /// <summary>
    /// Správný nástroj musí kopat znatelně rychleji, jinak nemá smysl ho vyrábět.
    /// </summary>
    [Fact]
    public void Krumpac_kope_kamen_rychleji_nez_ruka()
    {
        BlockRegistry blocks = Blocks();
        ItemRegistry items = Items(blocks);
        var mining = new Mining(blocks, items);

        ushort stone = blocks.IndexOf("tesseris:stone");

        float hand = mining.BreakSeconds(stone, ItemStack.Empty);
        float flint = mining.BreakSeconds(stone, new ItemStack(items.IndexOf("tesseris:flint_pickaxe"), 1, 0));
        float diamond = mining.BreakSeconds(stone, new ItemStack(items.IndexOf("tesseris:diamond_pickaxe"), 1, 0));

        Assert.True(flint < hand, $"Pazourkový krumpáč ({flint} s) není rychlejší než ruka ({hand} s).");
        Assert.True(diamond < flint, $"Diamantový ({diamond} s) není rychlejší než pazourkový ({flint} s).");
    }

    /// <summary>
    /// Z kamene holou rukou nic nevypadne, ale s krumpáčem ano. To je celý smysl stupňů.
    /// </summary>
    [Fact]
    public void Kamen_bez_krumpace_nic_nevydá()
    {
        BlockRegistry blocks = Blocks();
        ItemRegistry items = Items(blocks);
        var mining = new Mining(blocks, items);

        ushort stone = blocks.IndexOf("tesseris:stone");

        Assert.False(mining.CanHarvest(stone, ItemStack.Empty));
        Assert.True(mining.CanHarvest(stone, new ItemStack(items.IndexOf("tesseris:flint_pickaxe"), 1, 0)));

        // Lopata je špatný nástroj, i když je z diamantu.
        Assert.False(mining.CanHarvest(stone, new ItemStack(items.IndexOf("tesseris:diamond_shovel"), 1, 0)));
    }

    [Fact]
    public void Rudy_vynucuji_postup_a_pousteji_suroviny()
    {
        BlockRegistry blocks = Blocks();
        ItemRegistry items = Items(blocks);
        var mining = new Mining(blocks, items);

        var flint = new ItemStack(items.IndexOf("tesseris:flint_pickaxe"), 1, 0);
        var stone = new ItemStack(items.IndexOf("tesseris:stone_pickaxe"), 1, 0);
        var iron = new ItemStack(items.IndexOf("tesseris:iron_pickaxe"), 1, 0);

        ushort coalOre = blocks.IndexOf("tesseris:coal_ore");
        ushort ironOre = blocks.IndexOf("tesseris:iron_ore");
        ushort diamondOre = blocks.IndexOf("tesseris:diamond_ore");

        Assert.False(mining.CanHarvest(coalOre, flint));
        Assert.False(mining.CanHarvest(ironOre, flint));
        Assert.True(mining.CanHarvest(coalOre, stone));
        Assert.True(mining.CanHarvest(ironOre, stone));
        Assert.False(mining.CanHarvest(diamondOre, stone));
        Assert.True(mining.CanHarvest(diamondOre, iron));

        Assert.Equal("tesseris:coal", blocks.Definition(coalOre).DropItem);
        Assert.Equal("tesseris:raw_iron", blocks.Definition(ironOre).DropItem);
        Assert.Equal("tesseris:diamond", blocks.Definition(diamondOre).DropItem);
    }

    [Fact]
    public void Kamen_a_drevo_se_spatnym_nastrojem_vubec_neznici()
    {
        BlockRegistry blocks = Blocks();
        ItemRegistry items = Items(blocks);
        var mining = new Mining(blocks, items);
        var axe = new ItemStack(items.IndexOf("tesseris:stone_axe"), 1, 0);
        var pickaxe = new ItemStack(items.IndexOf("tesseris:stone_pickaxe"), 1, 0);
        ushort stone = blocks.IndexOf("tesseris:stone");
        ushort log = blocks.IndexOf("tesseris:oak_log");

        Assert.True(float.IsPositiveInfinity(mining.BreakSeconds(stone, axe)));
        Assert.False(mining.Update(new OpenTK.Mathematics.Vector3i(0, 0, 0), stone, axe, 60.0, creative: false));
        Assert.Equal(0f, mining.Progress);

        Assert.True(float.IsPositiveInfinity(mining.BreakSeconds(log, pickaxe)));
        Assert.False(mining.Update(new OpenTK.Mathematics.Vector3i(1, 0, 0), log, pickaxe, 60.0, creative: false));
        Assert.Equal(0f, mining.Progress);

        Assert.True(mining.CanHarvest(stone, pickaxe));
        Assert.True(mining.CanHarvest(log, axe));
    }

    [Fact]
    public void Flintove_a_kamenne_nastroje_maji_spravne_prvni_recepty()
    {
        BlockRegistry blocks = Blocks();
        ItemRegistry items = Items(blocks);
        RecipeBook recipes = RecipeBook.Load(
            items, Path.Combine(AppContext.BaseDirectory, "assets", "recipes"));

        foreach ((string tool, int stones, int sticks) in new[]
        {
            ("stone_pickaxe", 3, 2),
            ("stone_axe", 3, 2),
            ("stone_sword", 2, 1),
            ("stone_shovel", 1, 2),
        })
        {
            RecipeBook.Entry recipe = Assert.Single(
                recipes.Crafting, entry => entry.Output == items.IndexOf($"tesseris:{tool}"));

            Assert.Contains(recipe.Inputs, input =>
                input.Item == items.IndexOf("tesseris:cobblestone") && input.Count == stones);
            Assert.Contains(recipe.Inputs, input =>
                input.Item == items.IndexOf("tesseris:stick") && input.Count == sticks);
        }

        foreach ((string tool, int flints, int sticks) in new[]
        {
            ("flint_pickaxe", 3, 2), ("flint_axe", 3, 2),
            ("flint_sword", 2, 1), ("flint_shovel", 1, 2),
        })
        {
            RecipeBook.Entry recipe = Assert.Single(
                recipes.Crafting, entry => entry.Output == items.IndexOf($"tesseris:{tool}"));
            Assert.Contains(recipe.Inputs, input =>
                input.Item == items.IndexOf("tesseris:flint") && input.Count == flints);
            Assert.Contains(recipe.Inputs, input =>
                input.Item == items.IndexOf("tesseris:stick") && input.Count == sticks);
        }

        Assert.DoesNotContain(
            recipes.Smelting,
            entry => entry.Output == items.IndexOf("tesseris:stone"));
        Assert.Contains(
            recipes.Smelting,
            entry => entry.Output == items.IndexOf("tesseris:iron_ingot"));
    }

    [Fact]
    public void Osm_dlazebnich_kostek_vyrobi_palivovou_kamennou_pec_ne_elektrickou()
    {
        BlockRegistry blocks = Blocks();
        ItemRegistry items = Items(blocks);
        RecipeBook recipes = RecipeBook.Load(
            items, Path.Combine(AppContext.BaseDirectory, "assets", "recipes"));

        RecipeBook.Entry recipe = Assert.Single(
            recipes.Crafting,
            entry => entry.Output == items.IndexOf("tesseris:stone_furnace"));

        Assert.Equal(1, recipe.Count);
        Assert.Contains(recipe.Inputs, input =>
            input.Item == items.IndexOf("tesseris:cobblestone") && input.Count == 8);
        Assert.DoesNotContain(
            recipes.Crafting,
            entry => entry.Output == items.IndexOf("tesseris:furnace"));
    }

    [Fact]
    public void Sekery_na_kmen_potrebuji_zamysleny_pocet_svihu()
    {
        BlockRegistry blocks = Blocks();
        ItemRegistry items = Items(blocks);
        var mining = new Mining(blocks, items);
        ushort log = blocks.IndexOf("tesseris:oak_log");
        const float swingSeconds = 1f / 2.2f;

        float stone = mining.BreakSeconds(log, new ItemStack(items.IndexOf("tesseris:stone_axe"), 1, 0));
        float diamond = mining.BreakSeconds(log, new ItemStack(items.IndexOf("tesseris:diamond_axe"), 1, 0));

        Assert.InRange(stone / swingSeconds, 8f, 9f);
        Assert.InRange(diamond / swingSeconds, 1.9f, 2.1f);
    }

    /// <summary>Hlína jde i rukou. Tvrdší podmínky nese jen kámen.</summary>
    [Fact]
    public void Hlina_jde_i_rukou()
    {
        BlockRegistry blocks = Blocks();
        ItemRegistry items = Items(blocks);
        var mining = new Mining(blocks, items);

        Assert.True(mining.CanHarvest(blocks.IndexOf("tesseris:dirt"), ItemStack.Empty));
    }

    /// <summary>
    /// Přesun cíle zahodí postup. Bez toho by šlo kopat do jednoho bloku, přejet na druhý
    /// a ten druhý rozbít okamžitě.
    /// </summary>
    [Fact]
    public void Presun_cile_zahodi_postup()
    {
        BlockRegistry blocks = Blocks();
        ItemRegistry items = Items(blocks);
        var mining = new Mining(blocks, items);

        ushort dirt = blocks.IndexOf("tesseris:dirt");

        mining.Update(new OpenTK.Mathematics.Vector3i(0, 0, 0), dirt, ItemStack.Empty, 0.2, creative: false);
        Assert.True(mining.Progress > 0f);

        mining.Update(new OpenTK.Mathematics.Vector3i(1, 0, 0), dirt, ItemStack.Empty, 0.0, creative: false);
        Assert.Equal(0f, mining.Progress);
    }

    [Fact]
    public void V_kreativu_padne_blok_hned()
    {
        BlockRegistry blocks = Blocks();
        ItemRegistry items = Items(blocks);
        var mining = new Mining(blocks, items);

        bool done = mining.Update(
            new OpenTK.Mathematics.Vector3i(0, 0, 0),
            blocks.IndexOf("tesseris:stone"),
            ItemStack.Empty,
            deltaSeconds: 0.001,
            creative: true);

        Assert.True(done);
    }
}
