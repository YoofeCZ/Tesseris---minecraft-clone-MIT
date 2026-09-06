using System.Text.Json;
using Tesseris.Game.Content;

namespace Tesseris.Game.Items;

/// <summary>Jedna surovina receptu.</summary>
public sealed class RecipeInput
{
    public string Item { get; set; } = string.Empty;

    public int Count { get; set; } = 1;
}

/// <summary>
/// Recept: co je potřeba a co z toho bude.
/// </summary>
/// <remarks>
/// <b>Bez mřížky.</b> Skládání do tvaru byla jedna z možností, ale zadavatel zvolil seznam:
/// hráč vidí, co si z toho, co má, může vyrobit, a klikne na to. Recept proto nenese
/// rozložení, jen množství.
/// </remarks>
public sealed class Recipe
{
    public string Id { get; set; } = string.Empty;

    /// <summary>Co vznikne.</summary>
    public string Output { get; set; } = string.Empty;

    public int Count { get; set; } = 1;

    public List<RecipeInput> Inputs { get; set; } = [];

    /// <summary>
    /// Taví se to v peci místo výroby z ruky?
    /// </summary>
    /// <remarks>
    /// Tavení je týž vztah surovina → výsledek, jen trvá a spotřebovává palivo. Držet ho
    /// ve zvláštním seznamu by znamenalo dvě cesty pro totéž.
    /// </remarks>
    public bool Smelting { get; set; }
}

/// <summary>
/// Všechny recepty ve hře, načtené z <c>assets/recipes</c>.
/// </summary>
/// <remarks>
/// <para>Data, ne kód. Přidat recept znamená přidat soubor, ne překládat hru — a hlavně
/// se tím recepty dají přečíst bez čtení programu.</para>
///
/// <para>Identifikátory se při načtení převedou na indexy. Hledat předmět podle jména
/// při každém kliknutí by znamenalo porovnávat řetězce v obsluze myši.</para>
/// </remarks>
public sealed class RecipeBook
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Recept s indexy místo jmen.</summary>
    public readonly record struct Entry(
        int Output, int Count, (int Item, int Count)[] Inputs, bool Smelting, string Name);

    private readonly Entry[] _entries;

    private RecipeBook(Entry[] entries) => _entries = entries;

    /// <summary>Recepty vyrobitelné z ruky.</summary>
    public IReadOnlyList<Entry> Crafting { get; private set; } = [];

    /// <summary>Recepty pece.</summary>
    public IReadOnlyList<Entry> Smelting { get; private set; } = [];

    public static RecipeBook Load(ItemRegistry items, string directory)
    {
        ArgumentNullException.ThrowIfNull(items);

        IReadOnlyList<ContentFile> files = [];
        if (Directory.Exists(directory))
        {
            var info = new DirectoryInfo(Path.GetFullPath(directory));
            string root = info.Parent?.FullName
                ?? throw new InvalidDataException($"Adresář s recepty nemá nadřazený adresář: {directory}");
            var catalog = new ContentCatalog([new ContentSource("tesseris", root, LegacyFlat: true)]);
            files = catalog.GetFiles(info.Name);
        }

        return LoadFiles(items, files);
    }

    /// <summary>Načte recepty ze všech zdrojů katalogu.</summary>
    public static RecipeBook Load(ItemRegistry items, ContentCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(catalog);
        return LoadFiles(items, catalog.GetFiles("recipes"));
    }

    private static RecipeBook LoadFiles(ItemRegistry items, IReadOnlyList<ContentFile> files)
    {
        ArgumentNullException.ThrowIfNull(items);

        List<Entry> entries = [];
        var recipeFiles = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (ContentFile file in files)
        {
            string path = file.FullPath;
            Recipe recipe = JsonSerializer.Deserialize<Recipe>(File.ReadAllText(path), JsonOptions)
                ?? throw new InvalidDataException($"Soubor {path} neobsahuje recept.");

            string recipeId;
            if (string.IsNullOrWhiteSpace(recipe.Id))
            {
                if (file.Source.LegacyFlat)
                {
                    throw new InvalidDataException($"Soubor {path} nemá vyplněné 'id'.");
                }

                recipeId = file.Id.ToString();
            }
            else
            {
                recipeId = NormalizeDefinitionId(recipe.Id, file);
            }

            if (!recipeFiles.TryAdd(recipeId, path))
            {
                throw new InvalidDataException(
                    $"Recept '{recipeId}' je definovaný víckrát: " +
                    $"'{recipeFiles[recipeId]}' a '{path}'.");
            }

            recipe.Output = NormalizeReference(recipe.Output, file.Source.Namespace);
            foreach (RecipeInput input in recipe.Inputs)
            {
                input.Item = NormalizeReference(input.Item, file.Source.Namespace);
            }

            int output = items.IndexOf(recipe.Output);

            if (output == ItemRegistry.Nothing)
            {
                throw new InvalidDataException($"Recept {path} vyrábí neznámý předmět {recipe.Output}.");
            }

            var inputs = new (int Item, int Count)[recipe.Inputs.Count];

            for (int i = 0; i < inputs.Length; i++)
            {
                int item = items.IndexOf(recipe.Inputs[i].Item);

                if (item == ItemRegistry.Nothing)
                {
                    throw new InvalidDataException(
                        $"Recept {path} potřebuje neznámý předmět {recipe.Inputs[i].Item}.");
                }

                inputs[i] = (item, Math.Max(1, recipe.Inputs[i].Count));
            }

            entries.Add(new Entry(
                output,
                Math.Max(1, recipe.Count),
                inputs,
                recipe.Smelting,
                items.Definition(output).Name));
        }

        // Setřídit podle jména, aby seznam nezávisel na pořadí souborů v adresáři —
        // to se liší podle systému a hráč by měl recepty pokaždé jinde.
        entries.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));

        var book = new RecipeBook([.. entries]);

        book.Crafting = [.. entries.Where(e => !e.Smelting)];
        book.Smelting = [.. entries.Where(e => e.Smelting)];

        return book;
    }

    private static string NormalizeDefinitionId(string value, ContentFile file)
    {
        ResourceId id;
        try
        {
            id = ResourceId.Resolve(value, file.Source.Namespace);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException($"Recept v souboru {file.FullPath} má neplatné 'id' {value}.", exception);
        }

        if (!file.Source.LegacyFlat && !string.Equals(id.Namespace, file.Source.Namespace, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Recept '{id}' v souboru {file.FullPath} nemůže definovat cizí namespace; " +
                $"zdroj vlastní '{file.Source.Namespace}'.");
        }

        return id.ToString();
    }

    private static string NormalizeReference(string value, string sourceNamespace)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        try
        {
            return ResourceId.Resolve(value, sourceNamespace).ToString();
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException($"Neplatný odkaz na resource '{value}'.", exception);
        }
    }

    /// <summary>Má hráč na tenhle recept suroviny?</summary>
    public static bool CanMake(Inventory inventory, in Entry entry)
    {
        ArgumentNullException.ThrowIfNull(inventory);

        foreach ((int item, int count) in entry.Inputs)
        {
            if (inventory.CountOf(item) < count)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Vyrobí předmět: odečte suroviny a přidá výsledek.
    /// </summary>
    /// <remarks>
    /// <para><b>Suroviny se odečítají až po kontrole, že je výsledek kam dát.</b> Jinak by
    /// se při plném inventáři suroviny snědly a nic by nevzniklo.</para>
    ///
    /// <para>Odečítání je všechno nebo nic, viz <c>Inventory.Remove</c>.</para>
    /// </remarks>
    public static bool Make(Inventory inventory, in Entry entry, ItemEntities? overflow, OpenTK.Mathematics.Vector3 where)
    {
        ArgumentNullException.ThrowIfNull(inventory);

        if (!CanMake(inventory, entry))
        {
            return false;
        }

        foreach ((int item, int count) in entry.Inputs)
        {
            inventory.Remove(item, count);
        }

        ItemStack left = inventory.Add(new ItemStack(entry.Output, entry.Count, 0));

        // Co se nevešlo, spadne na zem. Zahodit to by znamenalo, že výroba při plném
        // inventáři suroviny sní a nic nevrátí.
        if (!left.IsEmpty)
        {
            overflow?.Spawn(left, where, OpenTK.Mathematics.Vector3.UnitY * 2f);
        }

        return true;
    }
}
