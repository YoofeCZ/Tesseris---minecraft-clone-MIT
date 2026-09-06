using Tesseris.Game.Items;

namespace Tesseris.Game.UI;

/// <summary>Překlady rozhraní, které zůstává viditelné po vstupu do světa.</summary>
public static class GameLocalization
{
    public static bool IsEnglish(StartupLanguage language) => language == StartupLanguage.English;

    public static string Inventory(StartupLanguage language) => IsEnglish(language) ? "INVENTORY" : "INVENTAR";
    public static string Furnace(StartupLanguage language) => IsEnglish(language) ? "FURNACE" : "PEC";
    public static string Chest(StartupLanguage language) => IsEnglish(language) ? "CHEST" : "TRUHLA";
    public static string Burning(StartupLanguage language) => IsEnglish(language) ? "BURNING" : "HORI";
    public static string TurnOn(StartupLanguage language) => IsEnglish(language) ? "TURN ON" : "ZAPNOUT";
    public static string Items(StartupLanguage language) => IsEnglish(language) ? "ITEMS" : "PREDMETY";
    public static string RecipeBook(StartupLanguage language) => IsEnglish(language) ? "RECIPE BOOK" : "RECEPTAR";
    public static string Crafting(StartupLanguage language) => IsEnglish(language) ? "CRAFTING" : "VYROBA";
    public static string CloseRecipeBook(StartupLanguage language) => IsEnglish(language) ? "close recipe book" : "zavrit receptar";
    public static string Owns(StartupLanguage language, int count) => IsEnglish(language) ? $"have {count}" : $"mas {count}";
    public static string Search(StartupLanguage language) => IsEnglish(language) ? "SEARCH..." : "HLEDAT...";

    public static string ArmourSlot(StartupLanguage language, int slot) => (language, slot) switch
    {
        (StartupLanguage.English, 0) => "head", (StartupLanguage.English, 1) => "chest",
        (StartupLanguage.English, 2) => "legs", (StartupLanguage.English, _) => "boots",
        (_, 0) => "hlava", (_, 1) => "hrud", (_, 2) => "nohy", _ => "boty",
    };

    /// <summary>Data předmětů jsou dosud česky; anglické UI proto nepřebírá jejich název naslepo.</summary>
    public static string ItemName(ItemDefinition item, StartupLanguage language)
    {
        if (!IsEnglish(language)) return item.Name;

        return item.Id switch
        {
            "tesseris:acacia_sapling" => "acacia sapling", "tesseris:apple" => "apple",
            "tesseris:birch_sapling" => "birch sapling", "tesseris:bucket" => "bucket",
            "tesseris:charcoal" => "charcoal", "tesseris:coal" => "coal", "tesseris:diamond" => "diamond",
            "tesseris:cobblestone" => "fractured stone",
            "tesseris:cobblestone_slab" => "fractured stone slab",
            "tesseris:cobblestone_stairs" => "fractured stone stairs",
            "tesseris:diamond_axe" => "diamond axe", "tesseris:diamond_boots" => "diamond boots",
            "tesseris:diamond_chestplate" => "diamond chestplate", "tesseris:diamond_helmet" => "diamond helmet",
            "tesseris:diamond_leggings" => "diamond leggings", "tesseris:diamond_pickaxe" => "diamond pickaxe",
            "tesseris:diamond_shovel" => "diamond shovel", "tesseris:diamond_sword" => "diamond sword",
            "tesseris:flint" => "flint", "tesseris:iron_axe" => "iron axe", "tesseris:iron_boots" => "iron boots",
            "tesseris:iron_chestplate" => "iron chestplate", "tesseris:iron_helmet" => "iron helmet",
            "tesseris:iron_ingot" => "iron ingot", "tesseris:iron_leggings" => "iron leggings",
            "tesseris:iron_pickaxe" => "iron pickaxe", "tesseris:iron_shovel" => "iron shovel",
            "tesseris:iron_sword" => "iron sword", "tesseris:maple_sapling" => "maple sapling",
            "tesseris:oak_sapling" => "oak sapling", "tesseris:raw_iron" => "raw iron",
            "tesseris:small_stone" => "small stone", "tesseris:spruce_sapling" => "spruce sapling",
            "tesseris:stick" => "stick", "tesseris:stone_axe" => "stone axe",
            "tesseris:stone_pickaxe" => "stone pickaxe", "tesseris:stone_shovel" => "stone shovel",
            "tesseris:stone_sword" => "stone sword", "tesseris:flint_axe" => "flint axe",
            "tesseris:flint_pickaxe" => "flint pickaxe", "tesseris:flint_shovel" => "flint shovel",
            "tesseris:flint_sword" => "flint sword", _ => FriendlyId(item.Id),
        };
    }

    private static string FriendlyId(string id)
    {
        int separator = id.LastIndexOf(':');
        return (separator >= 0 ? id[(separator + 1)..] : id).Replace('_', ' ');
    }
}
