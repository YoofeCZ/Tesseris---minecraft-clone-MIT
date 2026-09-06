using System.Globalization;
using System.Text;
using OpenTK.Mathematics;
using Tesseris.Engine.Rendering;
using Tesseris.Game.Items;

namespace Tesseris.Game.UI;

/// <summary>
/// Pás pod rukou, panel inventáře a seznam výroby.
/// </summary>
/// <remarks>
/// <para><b>Otevřený inventář je jedno okno, ne tři pruhy slotů.</b> První verze kreslila
/// batoh a pás každý zvlášť na svém místě obrazovky a vypadalo to jako hromada lišt —
/// hráč z toho nepoznal, co k čemu patří. Okno má rám, nadpis, batoh nahoře, pás dole
/// (oddělený mezerou, protože se chová jinak) a vedle nich výrobu.</para>
///
/// <para><b>Pás je uvnitř okna tentýž jako dole na obrazovce</b> — jsou to prvních devět
/// slotů téhož inventáře. Proto se dá při otevřeném okně přeskládat, což je celý smysl.</para>
///
/// <para>Rozměry se odvozují od výšky okna. Napevno zadané pixely vypadají dobře na jednom
/// rozlišení a na jiném jsou špendlíkové hlavičky.</para>
/// </remarks>
public sealed class InventoryScreen
{
    private static readonly Vector4 PanelColor = new(0.07f, 0.07f, 0.09f, 0.95f);
    private static readonly Vector4 PanelEdge = new(0.42f, 0.42f, 0.48f, 1f);
    private static readonly Vector4 SlotColor = new(0.20f, 0.20f, 0.24f, 0.95f);
    private static readonly Vector4 SlotEdge = new(0.05f, 0.05f, 0.06f, 1f);
    private static readonly Vector4 Selected = new(0.96f, 0.96f, 0.99f, 1f);
    private static readonly Vector4 RecipeReady = new(0.18f, 0.30f, 0.20f, 0.95f);
    private static readonly Vector4 RecipeShort = new(0.20f, 0.14f, 0.14f, 0.95f);
    private static readonly Vector4 PowerOn = new(0.22f, 0.52f, 0.26f, 1f);
    private static readonly Vector4 PowerOff = new(0.26f, 0.26f, 0.30f, 1f);

    /// <summary>Popisek má vlastní rám. Bez něj splýval text s ikonami pod sebou.</summary>
    private static readonly Vector4 TooltipColor = new(0.04f, 0.04f, 0.06f, 0.97f);
    private static readonly Vector4 TooltipEdge = new(0.55f, 0.55f, 0.66f, 1f);

    private readonly ItemRegistry _items;
    private readonly int[] _creativeItems;

    /// <summary>Vrstvy ikon, které nejsou předmět: kniha a obrysy výstroje.</summary>
    private int _bookLayer = -1;
    private int _playerLayer = -1;
    private int[] _armourHints = [];

    public InventoryScreen(ItemRegistry items)
    {
        _items = items ?? throw new ArgumentNullException(nameof(items));
        _creativeItems = Enumerable.Range(0, items.Count)
            .OrderBy(item => IsBuiltIn(items.Definition(item).Id))
            .ThenBy(item => items.Definition(item).Id, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool IsBuiltIn(string id) =>
        id.StartsWith("tesseris:", StringComparison.Ordinal)
        || id.StartsWith("voxelity:", StringComparison.Ordinal);

    /// <summary>
    /// Doplní ikony, které nejsou předmět. Volá se, až je atlas hotový.
    /// </summary>
    /// <param name="book">Vrstva ikony knihy receptů.</param>
    /// <param name="armourHints">Obrysy do prázdných slotů výstroje, shora dolů.</param>
    public void SetIcons(int book, int player, int[] armourHints)
    {
        _bookLayer = book;
        _playerLayer = player;
        _armourHints = armourHints ?? [];
    }

    /// <summary>
    /// Sloupec výstroje: hlava, hruď, nohy, boty.
    /// </summary>
    /// <remarks>
    /// Prázdný slot ukazuje bledý obrys toho, co do něj patří. Bez něj jsou to čtyři
    /// stejné čtverce a hráč musí zkoušet, kam co jde.
    /// </remarks>
    private void DrawArmour(SpriteRenderer sprites, Inventory inventory, float size, int width, int height)
    {
        // Silueta postavy za sloty. Dělá ze sloupce čtverců panáka, na kterého hráč věci
        // navléká — je to jen kresba, žádný slot na ní neleží.
        // SILUETA STOJÍ VEDLE SLOTŮ, NE POD NIMI.
        //
        // Slot je skoro neprůhledný, takže z panáka schovaného pod ním koukaly jen okraje
        // a vypadal jako šmouha. Teď má vlastní pruh vlevo a sloty jsou napravo od něj —
        // je z toho panák, na kterého hráč věci navléká.
        if (_playerLayer >= 0)
        {
            Vector2 top = ArmourCorner(0, width, height);
            Vector2 bottom = ArmourCorner(Inventory.ArmourSlots - 1, width, height);

            float tall = bottom.Y + size - top.Y;
            (float x, _, _, _) = Window(width, height);

            // Ikona se kreslí čtvercem a panák je v dlaždici uprostřed, takže se čtverec
            // vysoký přes celý sloupec musí vystředit na pruh, který mu patří.
            float centre = x + (size * 0.4f) + (size * PlayerWidth * 0.5f);

            sprites.DrawRect(
                centre - (size * PlayerWidth * 0.5f), top.Y, size * PlayerWidth, tall,
                new Vector4(0.13f, 0.13f, 0.16f, 0.9f));

            sprites.DrawIcon(centre - (tall * 0.5f), top.Y, tall, _playerLayer, 0.85f);
        }

        for (int index = 0; index < Inventory.ArmourSlots; index++)
        {
            Vector2 corner = ArmourCorner(index, width, height);
            ItemStack stack = inventory[Inventory.FirstArmourSlot + index];

            sprites.DrawRect(corner.X, corner.Y, size, size, SlotColor);
            sprites.DrawFrame(corner.X, corner.Y, size, size, MathF.Max(2f, size * 0.05f), SlotEdge);

            float inset = size * 0.16f;

            if (!stack.IsEmpty)
            {
                DrawItemIcon(sprites, stack.Item, corner.X + inset, corner.Y + inset, size - (2f * inset));
                continue;
            }

            if (index < _armourHints.Length && _armourHints[index] >= 0)
            {
                sprites.DrawIcon(
                    corner.X + inset, corner.Y + inset, size - (2f * inset), _armourHints[index], 0.25f);
            }
        }
    }

    public bool Open { get; set; }

    /// <summary>
    /// Pec, kterou má hráč otevřenou. Když je nastavená, zabere sloupec místo výroby.
    /// </summary>
    /// <remarks>
    /// Pec a výroba se nezobrazují najednou schválně: hráč u pece řeší tavení, ne výrobu,
    /// a dva seznamy vedle sebe by z okna udělaly nástěnku.
    /// </remarks>
    public Furnace? Furnace { get; set; }

    public ChestContainer? Chest { get; set; }

    /// <summary>O kolik je seznam výroby odrolovaný.</summary>
    public int RecipeScroll { get; set; }

    /// <summary>
    /// Kreativní režim: místo výroby se ukáže mřížka se všemi předměty.
    /// </summary>
    /// <remarks>
    /// <para>Do teď se v kreativu vybíralo z palety bloků na klávesách 1 až 9, takže se
    /// nedaly zkoušet nástroje ani suroviny — a bloků je čtyřicet devět, takže se do devíti
    /// kláves stejně nevešly.</para>
    ///
    /// <para>Pec má přednost před obojím: u pece hráč řeší tavení.</para>
    /// </remarks>
    public bool Creative { get; set; }

    /// <summary>O kolik řádků je mřížka předmětů odrolovaná.</summary>
    public int CreativeScroll { get; set; }

    public string SearchText { get; private set; } = string.Empty;

    public bool SearchFocused { get; set; }

    public bool SearchAvailable => Open
        && (RightColumn == Column.Creative || (RightColumn == Column.Recipes && BookOpen));

    public void AppendSearchText(string text)
    {
        if (!SearchAvailable || !SearchFocused || string.IsNullOrEmpty(text)) return;

        foreach (char character in text)
        {
            if (!char.IsControl(character) && SearchText.Length < 64)
            {
                SearchText += character;
            }
        }

        ResetSearchScroll();
    }

    public void BackspaceSearch()
    {
        if (!SearchAvailable || !SearchFocused || SearchText.Length == 0) return;
        int[] elements = StringInfo.ParseCombiningCharacters(SearchText);
        SearchText = elements.Length == 0 ? string.Empty : SearchText[..elements[^1]];
        ResetSearchScroll();
    }

    public void ClearSearch()
    {
        SearchText = string.Empty;
        ResetSearchScroll();
    }

    private void ResetSearchScroll()
    {
        CreativeScroll = 0;
        BookScroll = 0;
    }

    /// <summary>Kolik sloupců má mřížka předmětů. Buňka se dopočítá tak, aby sloupec vyplnila.</summary>
    private const int CreativeColumns = 5;

    /// <summary>
    /// Rozměry mřížky předmětů: velikost buňky, kolik se jich vejde a kde začínají.
    /// </summary>
    /// <remarks>
    /// <para><b>Počet řádků se počítá z místa, které v okně opravdu je</b>, ne napevno.
    /// S osmi řádky natvrdo mřížka přetékala pod spodní hranu okna a poslední předměty
    /// visely na trávě.</para>
    ///
    /// <para>Jeden výpočet pro kreslení i pro zkoušku zásahu. Kdyby byly dva, rozešly by se
    /// a klikalo by se vedle — což je chyba, kterou hráč popíše jako „nejde to".</para>
    /// </remarks>
    private (float Cell, int Rows, float Left, float Top) CreativeGrid(int width, int height)
    {
        float size = SlotSize(height);
        (float x, float y, float w, float h) = Window(width, height);

        float left = x + w - (size * 5.2f);
        float top = y + (size * 1.9f);

        // Buňka vyplní šířku sloupce; svisle se vejde tolik řádků, kolik zbylo místa.
        float cell = (size * 4.9f) / CreativeColumns;
        float room = h - (top - y) - (size * 0.4f);

        return (cell, Math.Max(1, (int)(room / cell)), left, top);
    }

    /// <summary>Kolik řádků má mřížka předmětů celkem. Podle toho se omezuje rolování.</summary>
    public int CreativeRowCount => (FilteredCreativeItems().Length + CreativeColumns - 1) / CreativeColumns;

    /// <summary>Nejvyšší smysluplné odrolování: poslední řádek zůstane vidět.</summary>
    public int CreativeMaxScroll(int width, int height) =>
        Math.Max(0, CreativeRowCount - CreativeGrid(width, height).Rows);

    public static float SlotSize(int height) => MathF.Max(30f, height * 0.052f);

    /// <summary>
    /// Šířka sloupce výstroje ve slotech, i s mezerou.
    /// </summary>
    /// <remarks>
    /// Vejde se do ní silueta postavy <b>a vedle ní</b> sloupec slotů. Původně byla úzká
    /// a silueta se kreslila pod sloty — jenže slot je skoro neprůhledný, takže z panáka
    /// koukaly jen okraje a vypadal jako šmouha.
    /// </remarks>
    private const float ArmourColumn = 2.5f;

    /// <summary>Kolik z toho sloupce zabere silueta. Zbytek jsou sloty.</summary>
    private const float PlayerWidth = 1.3f;

    /// <summary>
    /// Vnější rozměry okna inventáře.
    /// </summary>
    /// <remarks>
    /// Veřejné kvůli testu, který hlídá, že obsah z okna neleze ven. Mřížka předmětů to
    /// jednou dělala a poslední řádky visely na trávě.
    /// </remarks>
    public static (float X, float Y, float Width, float Height) Window(int width, int height)
    {
        float size = SlotSize(height);

        // Zleva výstroj, pak devět slotů batohu, vpravo sloupec výroby široký pět slotů.
        float inner = (size * ArmourColumn) + (size * Inventory.HotbarSlots) + (size * 5.4f);
        float tall = (size * 3f) + (size * 0.9f) + size + (size * 1.5f);

        return ((width - inner) * 0.5f - (size * 0.4f), (height - tall) * 0.45f, inner + (size * 0.8f), tall);
    }

    /// <summary>
    /// Levý horní roh slotu výstroje: 0 hlava, 1 hruď, 2 nohy, 3 boty.
    /// </summary>
    /// <remarks>
    /// Stojí ve sloupci vlevo od batohu, shora dolů v pořadí, v jakém je člověk na sobě má.
    /// Je to čitelnější než mřížka, protože poloha slotu sama říká, co do něj patří.
    /// </remarks>
    public Vector2 ArmourCorner(int index, int width, int height)
    {
        float size = SlotSize(height);
        (float x, _, _, _) = Window(width, height);

        // SVISLE SE ZAROVNÁVÁ NA ŘÁDKY MŘÍŽKY, ne na vlastní rozteč.
        //
        // Vlastní rozteč se s batohem rozešla a čtvrtý slot skončil někde mezi řádky.
        // Čtyři kusy výstroje a čtyři řádky panelu (tři batohu a pás) sedí na sebe přesně,
        // tak se bere y rovnou od nich.
        int row = Math.Clamp(index, 0, Inventory.ArmourSlots - 1);

        int reference = row < 3
            ? Inventory.HotbarSlots + (row * Inventory.HotbarSlots)
            : 0;

        // Sloty stojí NAPRAVO od siluety, ne přes ni.
        return new Vector2(x + (size * (0.4f + PlayerWidth)), SlotCorner(reference, width, height).Y);
    }

    /// <summary>Který slot výstroje leží pod kurzorem, nebo −1.</summary>
    public int ArmourAt(Vector2 mouse, int width, int height)
    {
        if (!Open)
        {
            return -1;
        }

        float size = SlotSize(height);

        for (int index = 0; index < Inventory.ArmourSlots; index++)
        {
            Vector2 corner = ArmourCorner(index, width, height);

            if (mouse.X >= corner.X && mouse.X < corner.X + size
                && mouse.Y >= corner.Y && mouse.Y < corner.Y + size)
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>Levý horní roh slotu. Platí pro obě mřížky i pro zavřené okno.</summary>
    public Vector2 SlotCorner(int slot, int width, int height)
    {
        float size = SlotSize(height);

        if (!Open)
        {
            // Zavřeno: jen pás, dole uprostřed obrazovky.
            float total = size * Inventory.HotbarSlots;
            return new Vector2(
                ((width - total) * 0.5f) + (slot * size),
                height - size - (size * 0.25f));
        }

        (float wx, float wy, _, _) = Window(width, height);

        // Batoh začíná až za sloupcem výstroje.
        float left = wx + (size * 0.4f) + (size * ArmourColumn);
        float top = wy + (size * 1.1f);

        if (slot >= Inventory.HotbarSlots)
        {
            int index = slot - Inventory.HotbarSlots;
            return new Vector2(left + ((index % Inventory.HotbarSlots) * size), top + ((index / Inventory.HotbarSlots) * size));
        }

        // Pás leží pod batohem, oddělený mezerou — chová se jinak, tak má být vidět jinde.
        return new Vector2(left + (slot * size), top + (size * 3f) + (size * 0.5f));
    }

    /// <summary>Který slot leží pod kurzorem, nebo −1.</summary>
    public int SlotAt(Vector2 mouse, int width, int height)
    {
        float size = SlotSize(height);
        int count = Open ? Inventory.TotalSlots : Inventory.HotbarSlots;

        for (int slot = 0; slot < count; slot++)
        {
            Vector2 corner = SlotCorner(slot, width, height);

            if (mouse.X >= corner.X && mouse.X < corner.X + size
                && mouse.Y >= corner.Y && mouse.Y < corner.Y + size)
            {
                return slot;
            }
        }

        return -1;
    }

    /// <summary>
    /// Co se právě ukazuje v pravém sloupci okna.
    /// </summary>
    /// <remarks>
    /// <b>Sloupec je jeden a obsahy tři</b> — výroba, mřížka předmětů v kreativu a pec.
    /// Každá zkouška zásahu se proto musí nejdřív zeptat, jestli je vůbec na řadě. Bez toho
    /// hlídala výroba celou plochu sloupce a spolkla kliky do pece i do mřížky: klikatelné
    /// zůstaly jen ty řádky mřížky, které přetékaly pod okno, protože tam už seznam receptů
    /// nesahal. Vypadalo to, že inventář nereaguje.
    /// </remarks>
    private enum Column
    {
        Recipes,
        Creative,
        Furnace,
        Chest,
    }

    private Column RightColumn => Chest is not null
        ? Column.Chest
        : Furnace is not null
        ? Column.Furnace
        : (Creative ? Column.Creative : Column.Recipes);

    private static (float Left, float Top, float Size) ChestGrid(int width, int height)
    {
        float size = SlotSize(height);
        (float x, float y, float w, _) = Window(width, height);
        // Stejně jako klasická velká truhla: 9 sloupců, 3 nebo 6 řad.
        float cell = (size * 4.9f) / 9f;
        return (x + w - (size * 5.2f), y + (size * 1.4f), cell);
    }

    public Vector2 ChestSlot(int index, int width, int height)
    {
        (float left, float top, float size) = ChestGrid(width, height);
        return new Vector2(left + ((index % 9) * size), top + ((index / 9) * size));
    }

    public int ChestSlotAt(Vector2 mouse, int width, int height)
    {
        if (!Open || RightColumn != Column.Chest) return -1;
        float size = ChestGrid(width, height).Size;
        for (int index = 0; index < Chest!.SlotCount; index++)
        {
            Vector2 corner = ChestSlot(index, width, height);
            if (mouse.X >= corner.X && mouse.X < corner.X + size
                && mouse.Y >= corner.Y && mouse.Y < corner.Y + size) return index;
        }
        return -1;
    }

    private void DrawChest(SpriteRenderer sprites, float size, int width, int height)
    {
        ChestContainer chest = Chest!;
        (_, _, size) = ChestGrid(width, height);
        for (int index = 0; index < chest.SlotCount; index++)
        {
            Vector2 corner = ChestSlot(index, width, height);
            ItemStack stack = chest[index];
            sprites.DrawRect(corner.X, corner.Y, size, size, SlotColor);
            sprites.DrawFrame(corner.X, corner.Y, size, size, MathF.Max(2f, size * 0.05f), SlotEdge);
            if (!stack.IsEmpty)
            {
                float inset = size * 0.16f;
                DrawItemIcon(sprites, stack.Item, corner.X + inset, corner.Y + inset, size - (2f * inset));
            }
        }
    }

    /// <summary>
    /// Je otevřená kniha receptů?
    /// </summary>
    /// <remarks>
    /// Výroba ukazuje jen to, na co hráč právě má — což je při hraní správně, ale nedá se
    /// z toho zjistit, <b>co by šlo vyrobit, kdyby měl</b>. Na to je kniha: mřížka všech
    /// výsledků včetně nedostupných; jméno a suroviny ukáže po najetí.
    /// </remarks>
    public bool BookOpen { get; set; }

    /// <summary>O kolik celých řádků je kniha odrolovaná.</summary>
    public int BookScroll { get; set; }

    /// <summary>Kde je kurzor. Okno ji nastavuje každý snímek kvůli popiskům.</summary>
    public Vector2 Pointer { get; set; }

    /// <summary>Kolik sloupců má mřížka výroby.</summary>
    private const int CraftColumns = 5;

    /// <summary>Kolik sloupců má ikonová mřížka knihy receptů.</summary>
    public const int BookColumns = 5;

    /// <summary>
    /// Recepty, na které hráč právě má suroviny.
    /// </summary>
    /// <remarks>
    /// <b>Výroba ukazuje jen dostupné.</b> Dřív to byl seznam všech receptů obarvený podle
    /// dostupnosti, takže se hráč prokousával deseti řádky „nemáš" a mezi nimi hledal ten
    /// jeden zelený. Teď je v mřížce jen to, na co má — a co by šlo, kdyby měl, ukáže kniha.
    /// </remarks>
    public static List<int> Available(Inventory inventory, RecipeBook recipes)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(recipes);

        List<int> available = [];

        for (int i = 0; i < recipes.Crafting.Count; i++)
        {
            if (RecipeBook.CanMake(inventory, recipes.Crafting[i]))
            {
                available.Add(i);
            }
        }

        return available;
    }

    /// <summary>Rozměry pravé ikonové mřížky. Jeden výpočet pro kreslení i klikání.</summary>
    private static (float Cell, int Rows, float Left, float Top) RightGrid(
        int width, int height, int columns)
    {
        float size = SlotSize(height);
        (float x, float y, float w, float h) = Window(width, height);

        float left = x + w - (size * 5.2f);
        float top = y + (size * 1.9f);

        float cell = (size * 4.9f) / columns;
        float room = h - (top - y) - (size * 0.4f);

        return (cell, Math.Max(1, (int)(room / cell)), left, top);
    }

    private static (float Cell, int Rows, float Left, float Top) CraftGrid(int width, int height) =>
        RightGrid(width, height, CraftColumns);

    private static (float Cell, int Rows, float Left, float Top) BookGrid(int width, int height) =>
        RightGrid(width, height, BookColumns);

    public (float X, float Y, float Width, float Height) SearchBox(int width, int height)
    {
        float size = SlotSize(height);
        (float x, float y, float w, _) = Window(width, height);
        return (x + w - (size * 5.2f), y + (size * 1.05f), size * 4.9f, size * 0.62f);
    }

    public bool SearchBoxAt(Vector2 mouse, int width, int height)
    {
        if (!SearchAvailable) return false;
        (float x, float y, float w, float h) = SearchBox(width, height);
        return mouse.X >= x && mouse.X < x + w && mouse.Y >= y && mouse.Y < y + h;
    }

    private int[] FilteredCreativeItems() =>
        _creativeItems.Where(SearchMatches).ToArray();

    private int[] FilteredRecipeIndices(RecipeBook recipes) =>
        Enumerable.Range(0, recipes.Crafting.Count)
            .Where(index => SearchMatches(recipes.Crafting[index].Output))
            .ToArray();

    public int FilteredRecipeCount(RecipeBook recipes)
    {
        ArgumentNullException.ThrowIfNull(recipes);
        return FilteredRecipeIndices(recipes).Length;
    }

    private bool SearchMatches(int item)
    {
        if (string.IsNullOrWhiteSpace(SearchText)) return true;
        ItemDefinition definition = _items.Definition(item);
        string haystack = SearchKey(definition.Name + " " + definition.Id);
        return SearchKey(SearchText)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .All(token => haystack.Contains(token, StringComparison.Ordinal));
    }

    private static string SearchKey(string value)
    {
        string decomposed = value.Normalize(NormalizationForm.FormD);
        var result = new System.Text.StringBuilder(decomposed.Length);
        bool separator = true;
        foreach (char character in decomposed)
        {
            UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(character);
            if (category == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(character))
            {
                result.Append(char.ToLowerInvariant(character));
                separator = false;
            }
            else if (!separator)
            {
                result.Append(' ');
                separator = true;
            }
        }
        return result.ToString().Trim().Normalize(NormalizationForm.FormC);
    }

    /// <summary>
    /// Kolikátý z dostupných receptů leží pod kurzorem, nebo −1.
    /// </summary>
    /// <remarks>Index je do seznamu z <see cref="Available"/>, ne do všech receptů.</remarks>
    public int RecipeAt(Vector2 mouse, int width, int height)
    {
        if (!Open || RightColumn != Column.Recipes || BookOpen)
        {
            return -1;
        }

        (float cell, int rows, float left, float top) = CraftGrid(width, height);

        int column = (int)((mouse.X - left) / cell);
        int row = (int)((mouse.Y - top) / cell);

        if (mouse.X < left || mouse.Y < top || column >= CraftColumns || row >= rows)
        {
            return -1;
        }

        return (row * CraftColumns) + column;
    }

    /// <summary>Obdélník tlačítka s knihou receptů.</summary>
    public (float X, float Y, float Width, float Height) BookButton(int width, int height)
    {
        float size = SlotSize(height);
        (float x, float y, float w, _) = Window(width, height);

        // Tlačítko je v záhlaví vedle názvu pravého panelu. Předtím začínalo uvnitř první
        // buňky kreativní mřížky, takže přes ni ležela ikona knihy a klik nebral předmět.
        return (x + w - (size * 0.95f), y + (size * 0.25f), size * 0.7f, size * 0.7f);
    }

    /// <summary>Leží kurzor na tlačítku s knihou?</summary>
    public bool BookButtonAt(Vector2 mouse, int width, int height)
    {
        if (!Open || RightColumn != Column.Recipes)
        {
            return false;
        }

        (float x, float y, float w, float h) = BookButton(width, height);

        return mouse.X >= x && mouse.X < x + w && mouse.Y >= y && mouse.Y < y + h;
    }

    /// <summary>Kolik slotů má panel pece dohromady.</summary>
    public const int FurnaceSlots = 1 + Furnace.InputSlots + Furnace.OutputSlots;

    /// <summary>
    /// Levý horní roh slotu pece.
    /// </summary>
    /// <remarks>
    /// <para>Pořadí je shora dolů, jak to vidí hráč: <b>0</b> palivo nahoře, <b>1–2</b>
    /// suroviny uprostřed, <b>3–5</b> výstup dole. Z rozvržení má být poznat, kudy věci
    /// pecí procházejí.</para>
    ///
    /// <para><b>Všechno se vejde do sloupce širokého tři sloty</b>, tedy do nejširšího
    /// řádku. Dřív se řádky centrovaly kolem bodu a vypínač se lepil napravo od paliva —
    /// vylezl tím z okna ven a visel na krajině. Pevný sloupec to řeší tím, že žádný prvek
    /// nemá kam přetéct.</para>
    /// </remarks>
    public Vector2 FurnaceSlot(int index, int width, int height)
    {
        (float left, float top, float size) = FurnaceColumn(width, height);

        (int row, int column, int columns) = index switch
        {
            0 => (0, 0, 1),
            < 3 => (1, index - 1, Furnace.InputSlots),
            _ => (2, index - 3, Furnace.OutputSlots),
        };

        // Palivo drží levý kraj, aby vedle něj zbylo místo na vypínač. Ostatní řádky se
        // ve sloupci centrují.
        float rowLeft = index == 0
            ? left
            : left + ((FurnaceWidth - columns) * size * 0.5f);

        return new Vector2(rowLeft + (column * size), top + (row * size * FurnaceRowPitch));
    }

    /// <summary>Jak široký je sloupec pece, ve slotech. Rozhoduje nejširší řádek.</summary>
    private const float FurnaceWidth = Furnace.OutputSlots;

    /// <summary>Rozteč řádků pece: slot, pod ním proužek a mezera.</summary>
    private const float FurnaceRowPitch = 1.55f;

    /// <summary>Levý horní roh sloupce pece a velikost slotu. Jeden zdroj pro všechno v peci.</summary>
    private static (float Left, float Top, float Size) FurnaceColumn(int width, int height)
    {
        float size = SlotSize(height);
        (float x, float y, float w, _) = Window(width, height);

        // Pravý sloupec okna je široký 4,9 slotu; sloupec pece se do něj vycentruje.
        float strip = x + w - (size * 5.2f);

        return (strip + ((4.9f - FurnaceWidth) * size * 0.5f), y + (size * 1.4f), size);
    }

    /// <summary>Který slot pece leží pod kurzorem, nebo −1.</summary>
    public int FurnaceSlotAt(Vector2 mouse, int width, int height)
    {
        if (!Open || RightColumn != Column.Furnace)
        {
            return -1;
        }

        float size = SlotSize(height);

        for (int index = 0; index < FurnaceSlots; index++)
        {
            Vector2 corner = FurnaceSlot(index, width, height);

            if (mouse.X >= corner.X && mouse.X < corner.X + size
                && mouse.Y >= corner.Y && mouse.Y < corner.Y + size)
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>
    /// Obdélník vypínače pece. Sedí napravo od paliva a končí přesně na kraji sloupce.
    /// </summary>
    /// <remarks>
    /// Šířka není libovolná: musí se do ní vejít nápis ZAPNOUT, tedy sedm znaků fontu
    /// osm na osm při měřítku odvozeném od velikosti slotu. Užší tlačítko by nápis ořízlo.
    /// </remarks>
    public (float X, float Y, float Width, float Height) FurnaceButton(int width, int height)
    {
        (float left, float top, float size) = FurnaceColumn(width, height);

        return (left + (size * 1.2f), top + (size * 0.15f), size * 1.8f, size * 0.7f);
    }

    /// <summary>Leží kurzor na vypínači pece?</summary>
    public bool FurnaceButtonAt(Vector2 mouse, int width, int height)
    {
        if (!Open || RightColumn != Column.Furnace)
        {
            return false;
        }

        (float x, float y, float w, float h) = FurnaceButton(width, height);

        return mouse.X >= x && mouse.X < x + w && mouse.Y >= y && mouse.Y < y + h;
    }

    /// <summary>Obsah slotu pece podle indexu z <see cref="FurnaceSlot"/>.</summary>
    public static ItemStack FurnaceContent(Furnace furnace, int index)
    {
        ArgumentNullException.ThrowIfNull(furnace);

        return index switch
        {
            0 => furnace.Fuel,
            < 3 => furnace.Input[index - 1],
            _ => furnace.Output[index - 3],
        };
    }

    private void DrawFurnace(SpriteRenderer sprites, float size, int width, int height)
    {
        Furnace furnace = Furnace!;

        for (int index = 0; index < FurnaceSlots; index++)
        {
            Vector2 corner = FurnaceSlot(index, width, height);
            ItemStack stack = FurnaceContent(furnace, index);

            sprites.DrawRect(corner.X, corner.Y, size, size, SlotColor);
            sprites.DrawFrame(corner.X, corner.Y, size, size, MathF.Max(2f, size * 0.05f), SlotEdge);

            if (!stack.IsEmpty)
            {
                float inset = size * 0.16f;
                DrawItemIcon(sprites, stack.Item, corner.X + inset, corner.Y + inset, size - (2f * inset));
            }
        }

        DrawFurnacePower(sprites, furnace, size, width, height);

        // Plamen pod palivem: kolik zbývá z rozdělaného kusu.
        Vector2 fuelCorner = FurnaceSlot(0, width, height);
        float flame = furnace.BurnTotal > 0f ? furnace.BurnLeft / furnace.BurnTotal : 0f;

        sprites.DrawRect(fuelCorner.X, fuelCorner.Y + size + (size * 0.12f), size, size * 0.2f, SlotEdge);
        sprites.DrawRect(
            fuelCorner.X, fuelCorner.Y + size + (size * 0.12f), size * Math.Clamp(flame, 0f, 1f), size * 0.2f,
            new Vector4(0.95f, 0.55f, 0.15f, 1f));

        // Postup tavení: proužek pod každým vstupním slotem zvlášť, protože každý slot
        // taví po svém a společný ukazatel by lhal.
        for (int slot = 0; slot < Furnace.InputSlots; slot++)
        {
            Vector2 corner = FurnaceSlot(1 + slot, width, height);
            float bar = corner.Y + size + (size * 0.12f);

            sprites.DrawRect(corner.X, bar, size, size * 0.2f, SlotEdge);
            sprites.DrawRect(
                corner.X, bar, size * Math.Clamp(furnace.Progress[slot], 0f, 1f), size * 0.2f,
                new Vector4(0.85f, 0.85f, 0.9f, 1f));
        }
    }

    /// <summary>
    /// Vypínač pece.
    /// </summary>
    /// <remarks>
    /// Zelený znamená „hoří", šedý „stojí". Barva musí rozhodnout dřív, než hráč přečte
    /// nápis — pec je jediná věc ve hře, která bez zapnutí nedělá nic, a tudíž jediná,
    /// u které se dá čekat, že se hráč bude ptát „proč se nic neděje".
    /// </remarks>
    private void DrawFurnacePower(
        SpriteRenderer sprites, Furnace furnace, float size, int width, int height)
    {
        (float x, float y, float w, float h) = FurnaceButton(width, height);

        sprites.DrawRect(x, y, w, h, furnace.Running ? PowerOn : PowerOff);
        sprites.DrawFrame(x, y, w, h, MathF.Max(2f, size * 0.05f), SlotEdge);
    }

    /// <summary>Nakreslí okno, sloty, ikony a výrobu. Text dopisuje <see cref="DrawText"/>.</summary>
    public void Draw(
        SpriteRenderer sprites, Inventory inventory, RecipeBook recipes, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(sprites);
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(recipes);

        float size = SlotSize(height);

        if (Open)
        {
            (float x, float y, float w, float h) = Window(width, height);

            sprites.DrawRect(x, y, w, h, PanelColor);
            sprites.DrawFrame(x, y, w, h, MathF.Max(2f, size * 0.06f), PanelEdge);

            if (SearchAvailable)
            {
                (float searchX, float searchY, float searchW, float searchH) = SearchBox(width, height);
                sprites.DrawRect(searchX, searchY, searchW, searchH, SlotColor);
                sprites.DrawFrame(
                    searchX, searchY, searchW, searchH,
                    MathF.Max(2f, size * 0.04f), SearchFocused ? Selected : SlotEdge);
            }

            switch (RightColumn)
            {
                case Column.Chest:
                    DrawChest(sprites, size, width, height);
                    break;

                case Column.Furnace:
                    DrawFurnace(sprites, size, width, height);
                    break;

                case Column.Creative:
                    DrawCreative(sprites, width, height);
                    break;

                default:
                    if (BookOpen)
                    {
                        DrawBook(sprites, inventory, recipes, width, height);
                    }
                    else
                    {
                        DrawRecipes(sprites, inventory, recipes, width, height);
                    }

                    break;
            }

            // Kniha je přepínač výroby, ne součást kreativního katalogu ani pece.
            if (RightColumn == Column.Recipes)
            {
                (float bx, float by, float bw, float bh) = BookButton(width, height);

                sprites.DrawRect(bx, by, bw, bh, BookOpen ? RecipeReady : SlotColor);
                sprites.DrawFrame(bx, by, bw, bh, MathF.Max(2f, size * 0.05f), SlotEdge);

                if (_bookLayer >= 0)
                {
                    float inset = bw * 0.14f;
                    sprites.DrawIcon(bx + inset, by + inset, bw - (2f * inset), _bookLayer);
                }
            }

            DrawArmour(sprites, inventory, size, width, height);

        }

        int count = Open ? Inventory.TotalSlots : Inventory.HotbarSlots;

        for (int slot = 0; slot < count; slot++)
        {
            DrawSlot(sprites, inventory, slot, SlotCorner(slot, width, height), size, slot == inventory.Selected);
        }
    }

    /// <summary>
    /// Mřížka ikon toho, co jde právě vyrobit.
    /// </summary>
    /// <remarks>
    /// Jen ikony, bez jmen: jméno se ukáže po najetí myší. Seznam se jmény byl při deseti
    /// receptech ještě čitelný, ale roste s každým přidaným — kdežto mřížka ikon roste do
    /// plochy a hráč v ní hledá tvar, ne text.
    /// </remarks>
    private void DrawRecipes(SpriteRenderer sprites, Inventory inventory, RecipeBook recipes, int width, int height)
    {
        (float cell, int rows, float left, float top) = CraftGrid(width, height);

        List<int> available = Available(inventory, recipes);

        float inset = cell * 0.14f;

        for (int i = 0; i < rows * CraftColumns && i < available.Count; i++)
        {
            float cx = left + ((i % CraftColumns) * cell);
            float cy = top + ((i / CraftColumns) * cell);

            bool hovered = Pointer.X >= cx && Pointer.X < cx + cell
                && Pointer.Y >= cy && Pointer.Y < cy + cell;

            sprites.DrawRect(cx + 1f, cy + 1f, cell - 2f, cell - 2f, hovered ? RecipeReady : SlotColor);

            DrawItemIcon(
                sprites, recipes.Crafting[available[i]].Output,
                cx + inset, cy + inset, cell - (2f * inset));
        }
    }

    /// <summary>
    /// Kniha receptů: všechny výsledky jako čistá mřížka ikon.
    /// </summary>
    /// <remarks>
    /// Jméno a suroviny patří do tooltipu po najetí. V hlavním panelu by z každého receptu
    /// jinak vznikl dlouhý textový řádek a hráč by místo hledání tvaru četl useknuté názvy.
    /// </remarks>
    private void DrawBook(
        SpriteRenderer sprites, Inventory inventory, RecipeBook recipes,
        int width, int height)
    {
        (float cell, int rows, float left, float top) = BookGrid(width, height);
        int[] filtered = FilteredRecipeIndices(recipes);

        float inset = cell * 0.14f;

        for (int i = 0; i < rows * BookColumns; i++)
        {
            int index = (BookScroll * BookColumns) + i;

            if (index >= filtered.Length)
            {
                break;
            }

            RecipeBook.Entry entry = recipes.Crafting[filtered[index]];
            bool ready = RecipeBook.CanMake(inventory, entry);

            float cx = left + ((i % BookColumns) * cell);
            float cy = top + ((i / BookColumns) * cell);
            bool hovered = Pointer.X >= cx && Pointer.X < cx + cell
                && Pointer.Y >= cy && Pointer.Y < cy + cell;

            // V klidu neutrální mřížka čistých ikon. Barva dostupnosti se ukáže až při
            // najetí a nedostupný recept zůstane rozeznatelný nižším jasem ikony.
            Vector4 background = hovered
                ? (ready ? RecipeReady : RecipeShort)
                : SlotColor;

            sprites.DrawRect(cx + 1f, cy + 1f, cell - 2f, cell - 2f, background);
            DrawItemIcon(
                sprites, entry.Output,
                cx + inset, cy + inset, cell - (2f * inset),
                ready ? 1f : 0.5f);
        }
    }

    /// <summary>Který recept v ikonové mřížce leží pod kurzorem, nebo −1.</summary>
    public int BookRowAt(Vector2 mouse, int width, int height)
    {
        if (!Open || !BookOpen || RightColumn != Column.Recipes)
        {
            return -1;
        }

        (float cell, int rows, float left, float top) = BookGrid(width, height);

        if (mouse.X < left || mouse.Y < top)
        {
            return -1;
        }

        int column = (int)((mouse.X - left) / cell);
        int row = (int)((mouse.Y - top) / cell);

        if (column < 0 || column >= BookColumns || row < 0 || row >= rows)
        {
            return -1;
        }

        return ((BookScroll + row) * BookColumns) + column;
    }

    public int BookRecipeAt(Vector2 mouse, RecipeBook recipes, int width, int height)
    {
        int position = BookRowAt(mouse, width, height);
        if (position < 0) return -1;
        int[] filtered = FilteredRecipeIndices(recipes);
        return position < filtered.Length ? filtered[position] : -1;
    }

    /// <summary>Nejvyšší řádek, na který lze knihu odrolovat.</summary>
    public int BookMaxScroll(int recipeCount, int width, int height)
    {
        (_, int visibleRows, _, _) = BookGrid(width, height);
        int totalRows = (Math.Max(0, recipeCount) + BookColumns - 1) / BookColumns;

        return Math.Max(0, totalRows - visibleRows);
    }

    /// <summary>
    /// Který předmět leží pod kurzorem v kreativní mřížce, nebo <see cref="ItemRegistry.Nothing"/>.
    /// </summary>
    public int CreativeAt(Vector2 mouse, int width, int height)
    {
        if (!Open || RightColumn != Column.Creative)
        {
            return ItemRegistry.Nothing;
        }

        (float cell, int rows, float left, float top) = CreativeGrid(width, height);

        int column = (int)((mouse.X - left) / cell);
        int row = (int)((mouse.Y - top) / cell);

        if (mouse.X < left || mouse.Y < top || column >= CreativeColumns || row >= rows)
        {
            return ItemRegistry.Nothing;
        }

        int position = ((CreativeScroll + row) * CreativeColumns) + column;
        return CreativeItemAt(position);
    }

    /// <summary>Položka v seřazeném kreativním katalogu; modové namespace jsou před vestavěným obsahem.</summary>
    public int CreativeItemAt(int position)
    {
        int[] filtered = FilteredCreativeItems();
        return position >= 0 && position < filtered.Length
            ? filtered[position]
            : ItemRegistry.Nothing;
    }

    private void DrawCreative(SpriteRenderer sprites, int width, int height)
    {
        (float cell, int rows, float left, float top) = CreativeGrid(width, height);
        int[] filtered = FilteredCreativeItems();

        float inset = cell * 0.14f;

        for (int row = 0; row < rows; row++)
        {
            for (int column = 0; column < CreativeColumns; column++)
            {
                int position = ((CreativeScroll + row) * CreativeColumns) + column;

                if (position >= filtered.Length)
                {
                    return;
                }

                int item = filtered[position];

                float cx = left + (column * cell);
                float cy = top + (row * cell);

                sprites.DrawRect(cx + 1f, cy + 1f, cell - 2f, cell - 2f, SlotColor);
                DrawItemIcon(sprites, item, cx + inset, cy + inset, cell - (2f * inset));
            }
        }
    }

    private void DrawSlot(
        SpriteRenderer sprites, Inventory inventory, int slot, Vector2 corner, float size, bool selected)
    {
        float inset = size * 0.06f;
        float inner = size - (2f * inset);

        sprites.DrawRect(corner.X + inset, corner.Y + inset, inner, inner, SlotColor);
        sprites.DrawFrame(
            corner.X + inset, corner.Y + inset, inner, inner,
            MathF.Max(2f, size * 0.05f), selected ? Selected : SlotEdge);

        ItemStack stack = inventory[slot];

        if (stack.IsEmpty)
        {
            return;
        }

        float iconInset = size * 0.16f;

        DrawItemIcon(
            sprites, stack.Item,
            corner.X + iconInset, corner.Y + iconInset, size - (2f * iconInset));

        DrawDurability(sprites, stack, corner, size);
    }

    /// <summary>
    /// Proužek opotřebení pod ikonou.
    /// </summary>
    /// <remarks>
    /// Barva jde od zelené k červené. Číslo se do slotu nevejde a stav nástroje musí jít
    /// poznat pohledem, ne počítáním.
    /// </remarks>
    private void DrawDurability(SpriteRenderer sprites, ItemStack stack, Vector2 corner, float size)
    {
        int durability = _items.Definition(stack.Item).Durability;

        if (durability <= 0 || stack.Damage <= 0)
        {
            return;
        }

        float left = 1f - (stack.Damage / (float)durability);

        float barWidth = size * 0.72f;
        float barHeight = MathF.Max(3f, size * 0.08f);
        float x = corner.X + (size * 0.14f);
        float y = corner.Y + size - (size * 0.22f);

        sprites.DrawRect(x, y, barWidth, barHeight, new Vector4(0.1f, 0.1f, 0.1f, 1f));
        sprites.DrawRect(x, y, barWidth * left, barHeight, new Vector4(1f - left, left, 0.15f, 1f));
    }

    /// <summary>
    /// Dopíše nadpisy, počty a jména receptů.
    /// </summary>
    /// <remarks>
    /// <b>Musí jít v JEDNÉ dávce se zbytkem textu.</b> Vykreslovač textu má jeden buffer
    /// na snímek, takže druhá dávka přepíše vrcholy té první dřív, než je grafika nakreslí
    /// — a z první zbude to, co bylo ve druhé. Přesně tak zmizel řádek s FPS.
    /// </remarks>
    public void DrawText(
        TextRenderer text, Inventory inventory, RecipeBook recipes, int width, int height, StartupLanguage language)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(recipes);

        float size = SlotSize(height);
        int scale = Math.Max(1, (int)(size / 26f));

        int count = Open ? Inventory.TotalSlots : Inventory.HotbarSlots;

        for (int slot = 0; slot < count; slot++)
        {
            ItemStack stack = inventory[slot];

            if (stack.IsEmpty || stack.Count <= 1)
            {
                continue;
            }

            Vector2 corner = SlotCorner(slot, width, height);
            string label = stack.Count.ToString(CultureInfo.InvariantCulture);

            text.DrawText(
                label,
                (int)(corner.X + size - (size * 0.12f) - (label.Length * Font8x8.GlyphWidth * scale)),
                (int)(corner.Y + size - (size * 0.12f) - (Font8x8.GlyphHeight * scale)),
                scale);
        }

        if (!Open)
        {
            return;
        }

        (float x, float y, float w, _) = Window(width, height);

        text.DrawText(GameLocalization.Inventory(language), (int)(x + (size * 0.4f)), (int)(y + (size * 0.35f)), scale);

        float left = x + w - (size * 5.2f);

        // Vzor místo porovnání s Column.Furnace: překladač z enumu neodvodí, že pec není null.
        if (Chest is { } chest)
        {
            text.DrawText(GameLocalization.Chest(language), (int)left, (int)(y + (size * 0.35f)), scale);
            float chestSize = ChestGrid(width, height).Size;
            for (int index = 0; index < chest.SlotCount; index++)
            {
                ItemStack stack = chest[index];
                if (stack.IsEmpty || stack.Count <= 1) continue;
                Vector2 corner = ChestSlot(index, width, height);
                string label = stack.Count.ToString(CultureInfo.InvariantCulture);
                text.DrawText(label,
                    (int)(corner.X + chestSize - (chestSize * 0.12f) - (label.Length * Font8x8.GlyphWidth * scale)),
                    (int)(corner.Y + chestSize - (chestSize * 0.12f) - (Font8x8.GlyphHeight * scale)), scale);
            }
            return;
        }

        if (Furnace is { } furnace)
        {
            text.DrawText(GameLocalization.Furnace(language), (int)left, (int)(y + (size * 0.35f)), scale);

            (float bx, float by, float bw, float bh) = FurnaceButton(width, height);

            // Nápis se centruje do tlačítka, jinak z něj u velkého rozlišení leze ven.
            string label = furnace.Running ? GameLocalization.Burning(language) : GameLocalization.TurnOn(language);

            text.DrawText(
                label,
                (int)(bx + ((bw - (label.Length * Font8x8.GlyphWidth * scale)) * 0.5f)),
                (int)(by + ((bh - (Font8x8.GlyphHeight * scale)) * 0.5f)),
                scale);

            DrawFurnaceCounts(text, furnace, width, height, size, scale);
            return;
        }

        if (RightColumn == Column.Creative)
        {
            text.DrawText(GameLocalization.Items(language), (int)left, (int)(y + (size * 0.35f)), scale);
            DrawSearchText(text, width, height, language, scale);
            return;
        }

        text.DrawText(BookOpen ? GameLocalization.RecipeBook(language) : GameLocalization.Crafting(language), (int)left, (int)(y + (size * 0.35f)), scale);

        if (BookOpen)
        {
            DrawSearchText(text, width, height, language, scale);
        }

    }

    private void DrawSearchText(
        TextRenderer text, int width, int height, StartupLanguage language, int scale)
    {
        (float x, float y, _, float h) = SearchBox(width, height);
        string value = SearchText.Length == 0 ? GameLocalization.Search(language) : SearchText;
        int textScale = Math.Max(1, scale - 1);
        text.DrawText(
            value,
            (int)(x + (SlotSize(height) * 0.16f)),
            (int)(y + ((h - (Font8x8.GlyphHeight * textScale)) * 0.5f)),
            textScale);
    }

    /// <summary>
    /// Nakreslí rám popisku pod kurzorem.
    /// </summary>
    /// <remarks>
    /// <para><b>Volá se až PO textu panelu</b>, tedy ve vlastní dávce spritů. Kdyby šel
    /// s ostatními, ležel by pod nadpisy a počty — sprity se kreslí před textem — a text
    /// by popiskem prosvítal.</para>
    ///
    /// <para>Text popisku se kreslí zvlášť, viz <see cref="DrawTooltipText"/>. Obojí bere
    /// rozvržení z téhož výpočtu.</para>
    /// </remarks>
    public void DrawTooltipFrame(
        SpriteRenderer sprites, Inventory inventory, RecipeBook recipes, int width, int height, StartupLanguage language)
    {
        ArgumentNullException.ThrowIfNull(sprites);

        float size = SlotSize(height);

        if (!TryTooltip(inventory, recipes, width, height, Math.Max(1, (int)(size / 26f)), language, out Tooltip tip))
        {
            return;
        }

        sprites.DrawRect(tip.X, tip.Y, tip.Width, tip.Height, TooltipColor);
        sprites.DrawFrame(tip.X, tip.Y, tip.Width, tip.Height, MathF.Max(2f, size * 0.04f), TooltipEdge);

        // Ikony patří do téže závěrečné dávky jako rám. Kdyby se kreslily s hlavním panelem,
        // pozdější rám tooltipu by je zakryl.
        for (int i = 0; i < tip.Lines.Length; i++)
        {
            TooltipLine line = tip.Lines[i];

            if (!line.HasIcon)
            {
                continue;
            }

            float iconY = tip.Y + tip.Padding + (i * tip.LineHeight)
                + ((tip.LineHeight - tip.IconSize) * 0.5f);

            DrawItemIcon(
                sprites, line.Item,
                tip.X + tip.Padding, iconY, tip.IconSize, line.Brightness);
        }
    }

    public void DrawItemIcon(
        SpriteRenderer sprites,
        int item,
        float x,
        float y,
        float size,
        float brightness = 1f)
    {
        if (_items.TryGetBlockIcon(item, out BlockItemIcon icon))
        {
            BlockItemIconRenderer.Draw(sprites, x, y, size, icon, brightness);
            return;
        }

        sprites.DrawIcon(x, y, size, _items.IconLayer(item), brightness);
    }

    /// <summary>Text popisku. Kreslí se do vlastní dávky po rámu, aby ležel na něm.</summary>
    public void DrawTooltipText(
        TextRenderer text, Inventory inventory, RecipeBook recipes, int width, int height, StartupLanguage language)
    {
        ArgumentNullException.ThrowIfNull(text);

        float size = SlotSize(height);

        if (!TryTooltip(inventory, recipes, width, height, Math.Max(1, (int)(size / 26f)), language, out Tooltip tip))
        {
            return;
        }

        for (int i = 0; i < tip.Lines.Length; i++)
        {
            TooltipLine line = tip.Lines[i];
            float iconIndent = line.HasIcon ? tip.IconSize + tip.IconGap : 0f;

            text.DrawText(
                line.Text,
                (int)(tip.X + tip.Padding + iconIndent),
                (int)(tip.Y + tip.Padding + (i * tip.LineHeight)),
                tip.Scale);
        }
    }

    /// <summary>Jeden řádek popisku. Surovina má vedle textu i ikonu předmětu.</summary>
    private readonly record struct TooltipLine(string Text, int Item, float Brightness)
    {
        public bool HasIcon => Item != ItemRegistry.Nothing;
    }

    /// <summary>Rozvržení popisku pod kurzorem společné pro rám, ikony i text.</summary>
    private readonly record struct Tooltip(
        TooltipLine[] Lines,
        float X,
        float Y,
        float Width,
        float Height,
        float LineHeight,
        float Padding,
        float IconSize,
        float IconGap,
        int Scale);

    /// <summary>Rozvržení tooltipu pro test bez grafického kontextu.</summary>
    public (int Lines, int Icons, bool IconsInsideFrame, bool TextClearsIcons) TooltipLayoutForTest(
        Inventory inventory, RecipeBook recipes, int width, int height)
    {
        float size = SlotSize(height);

        if (!TryTooltip(inventory, recipes, width, height, Math.Max(1, (int)(size / 26f)), StartupLanguage.Czech, out Tooltip tip))
        {
            return (0, 0, false, false);
        }

        int icons = 0;
        bool inside = true;
        bool clears = true;

        for (int i = 0; i < tip.Lines.Length; i++)
        {
            TooltipLine line = tip.Lines[i];

            if (!line.HasIcon)
            {
                continue;
            }

            icons++;

            float iconLeft = tip.X + tip.Padding;
            float iconTop = tip.Y + tip.Padding + (i * tip.LineHeight)
                + ((tip.LineHeight - tip.IconSize) * 0.5f);
            float iconRight = iconLeft + tip.IconSize;
            float iconBottom = iconTop + tip.IconSize;
            float textLeft = tip.X + tip.Padding + tip.IconSize + tip.IconGap;

            inside &= iconLeft >= tip.X && iconTop >= tip.Y
                && iconRight <= tip.X + tip.Width && iconBottom <= tip.Y + tip.Height;
            clears &= textLeft >= iconRight + tip.IconGap;
        }

        return (tip.Lines.Length, icons, inside, clears);
    }

    /// <summary>
    /// Spočítá, kde bude popisek a co v něm bude.
    /// </summary>
    /// <remarks>
    /// <para><b>Jeden výpočet pro rám i pro text.</b> Rám se kreslí sprity, text vlastním
    /// vykreslovačem — jsou to dvě různé dávky a musí se sejít na pixel, jinak by text
    /// ležel vedle svého pozadí.</para>
    ///
    /// <para><b>Odsazení od kurzoru je větší než sám kurzor.</b> Deset pixelů nestačilo:
    /// popisek u slotu výstroje končil přesně pod šipkou a nešel přečíst.</para>
    /// </remarks>
    private bool TryTooltip(
        Inventory inventory, RecipeBook recipes, int width, int height, int scale, StartupLanguage language, out Tooltip tip)
    {
        tip = default;

        TooltipLine[] lines = HoverLines(inventory, recipes, width, height, language);

        if (lines.Length == 0)
        {
            return false;
        }

        // Menší písmo: recept se surovinami má i padesát znaků a v běžném měřítku by to
        // byly tři čtvrtiny obrazovky.
        int small = Math.Max(1, scale - 1);

        float lineHeight = (Font8x8.GlyphHeight + 3) * small;
        float iconSize = lineHeight - (2f * small);
        float iconGap = 3f * small;
        float widest = 0f;

        foreach (TooltipLine line in lines)
        {
            float lineWidth = line.Text.Length * Font8x8.GlyphWidth * small;

            if (line.HasIcon)
            {
                lineWidth += iconSize + iconGap;
            }

            widest = MathF.Max(widest, lineWidth);
        }

        float padding = 5f * small;

        float boxWidth = widest + (padding * 2f);
        float boxHeight = (lines.Length * lineHeight) + (padding * 2f);

        // Kurzor je zhruba dvacet pixelů; popisek musí začít až za ním.
        float offset = 20f;

        float x = Pointer.X + offset;
        float y = Pointer.Y + offset;

        // Napravo od kurzoru, a když se to nevejde, tak nalevo. Pak se to ještě přiskřípne
        // k oknu, aby popisek nikdy nezmizel za okrajem.
        if (x + boxWidth > width)
        {
            x = Pointer.X - boxWidth - offset;
        }

        x = Math.Clamp(x, 4f, MathF.Max(4f, width - boxWidth - 4f));
        y = Math.Clamp(y, 4f, MathF.Max(4f, height - boxHeight - 4f));

        tip = new Tooltip(
            lines, x, y, boxWidth, boxHeight, lineHeight, padding, iconSize, iconGap, small);
        return true;
    }

    /// <summary>Co leží pod kurzorem. Recept nese text i ikonky svých surovin.</summary>
    private TooltipLine[] HoverLines(Inventory inventory, RecipeBook recipes, int width, int height, StartupLanguage language)
    {
        if (!Open)
        {
            return [];
        }

        if (BookButtonAt(Pointer, width, height))
        {
            return [TextLine(BookOpen ? GameLocalization.CloseRecipeBook(language) : GameLocalization.RecipeBook(language))];
        }

        // V RECEPTÁŘI SE UKAZUJE, CO JE NA TO POTŘEBA.
        //
        // Bylo hlášeno jako „je pěkný receptář, ale když na to namířím myší, nevidím, jak
        // to vykraftit". Ikony surovin samy nestačí: chybí na nich počet a hlavně to, kolik
        // jich hráč má. Popisek proto vypíše obojí.
        int bookRow = BookRecipeAt(Pointer, recipes, width, height);

        if (bookRow >= 0 && bookRow < recipes.Crafting.Count)
        {
            RecipeBook.Entry entry = recipes.Crafting[bookRow];

            // Jméno na první řádek, každá surovina na svůj. V závorce je, kolik jich hráč
            // má — bez toho se z popisku nedozví, co mu ještě chybí.
            var lines = new List<TooltipLine>
            {
                TextLine(RecipeName(entry, language)),
            };

            foreach ((int ingredient, int need) in entry.Inputs)
            {
                int owned = inventory.CountOf(ingredient);

                lines.Add(new TooltipLine(
                    $"{need}x {GameLocalization.ItemName(_items.Definition(ingredient), language)} ({GameLocalization.Owns(language, owned)})",
                    ingredient,
                    owned >= need ? 1f : 0.4f));
            }

            return [.. lines];
        }

        int recipe = RecipeAt(Pointer, width, height);

        if (recipe >= 0)
        {
            List<int> available = Available(inventory, recipes);

            if (recipe < available.Count)
            {
                RecipeBook.Entry entry = recipes.Crafting[available[recipe]];
                return [TextLine(RecipeName(entry, language))];
            }

            return [];
        }

        int armour = ArmourAt(Pointer, width, height);

        if (armour >= 0)
        {
            ItemStack worn = inventory[Inventory.FirstArmourSlot + armour];

            string label = worn.IsEmpty
                ? GameLocalization.ArmourSlot(language, armour)
                : GameLocalization.ItemName(_items.Definition(worn.Item), language);

            return [TextLine(label)];
        }

        int item = CreativeAt(Pointer, width, height);

        if (item != ItemRegistry.Nothing)
        {
            return [TextLine(GameLocalization.ItemName(_items.Definition(item), language))];
        }

        int slot = SlotAt(Pointer, width, height);

        if (slot >= 0 && !inventory[slot].IsEmpty)
        {
            return [TextLine(GameLocalization.ItemName(_items.Definition(inventory[slot].Item), language))];
        }

        return [];
    }

    private static TooltipLine TextLine(string text) =>
        new(text, ItemRegistry.Nothing, 1f);

    private string RecipeName(RecipeBook.Entry entry, StartupLanguage language)
    {
        string name = GameLocalization.ItemName(_items.Definition(entry.Output), language);
        return entry.Count > 1 ? $"{name} x{entry.Count}" : name;
    }

    /// <summary>Počty v slotech pece. Bez nich není poznat, kolik už se natavilo.</summary>
    private void DrawFurnaceCounts(
        TextRenderer text, Furnace furnace, int width, int height, float size, int scale)
    {
        for (int index = 0; index < FurnaceSlots; index++)
        {
            ItemStack stack = FurnaceContent(furnace, index);

            if (stack.IsEmpty || stack.Count <= 1)
            {
                continue;
            }

            Vector2 corner = FurnaceSlot(index, width, height);
            string label = stack.Count.ToString(CultureInfo.InvariantCulture);

            text.DrawText(
                label,
                (int)(corner.X + size - (size * 0.12f) - (label.Length * Font8x8.GlyphWidth * scale)),
                (int)(corner.Y + size - (size * 0.12f) - (Font8x8.GlyphHeight * scale)),
                scale);
        }
    }
}
