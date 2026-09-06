using System.Text.Json;
using Tesseris.Game.Blocks;
using Tesseris.Game.Content;

namespace Tesseris.Game.Items;

internal readonly record struct BlockItemIcon(byte Pieces, int TopLayer, int XLayer, int ZLayer);

/// <summary>
/// Seznam všech předmětů ve hře.
/// </summary>
/// <remarks>
/// <para>Vzniká ze dvou zdrojů. <b>Bloky se převádějí na předměty samy</b> — každý blok,
/// který jde položit, dostane předmět se stejným jménem. Psát ke každému bloku ještě
/// ruční definici by znamenalo dvě místa, která se musí držet v souladu, a to se rozejde
/// při prvním přidaném bloku.</para>
///
/// <para>Soubory v <c>assets/items</c> popisují jen to, co blok není: nástroje, suroviny
/// a kbelík. Načítají se <b>po</b> blocích, takže si můžou vzít index libovolného bloku.</para>
///
/// <para>Kapaliny předmět nedostanou. Voda je v registru bloků, ale nést ji v dlani nejde
/// — na to je kbelík, viz <c>Hotbar</c>.</para>
/// </remarks>
public sealed class ItemRegistry
{
    /// <summary>Index, který znamená „žádný předmět". Sedí na <see cref="ItemStack.Empty"/>.</summary>
    public const int Nothing = -1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly ItemDefinition[] _items;
    private readonly Dictionary<string, int> _byId;
    private readonly int[] _blockToItem;
    private readonly ushort[] _itemToBlock;

    private ItemRegistry(ItemDefinition[] items, BlockRegistry blocks)
    {
        _items = items;
        _blocks = blocks;

        _byId = new Dictionary<string, int>(items.Length, StringComparer.Ordinal);
        for (int i = 0; i < items.Length; i++)
        {
            _byId[items[i].Id] = i;
            if (items[i].Id.StartsWith("tesseris:", StringComparison.Ordinal))
            {
                _byId["voxelity:" + items[i].Id["tesseris:".Length..]] = i;
            }
        }

        // Obousměrný převod blok ↔ předmět. Drží se v polích, protože se na něj sahá
        // při každém rozbití i položení bloku.
        _blockToItem = new int[blocks.Count];
        Array.Fill(_blockToItem, Nothing);

        _itemToBlock = new ushort[items.Length];
        _flat = new bool[items.Length];

        for (int i = 0; i < items.Length; i++)
        {
            if (items[i].Kind != ItemKind.Block || string.IsNullOrEmpty(items[i].Block))
            {
                continue;
            }

            ushort block = blocks.IndexOf(items[i].Block);
            _itemToBlock[i] = block;

            if (block != BlockRegistry.Air)
            {
                _blockToItem[block] = i;
            }
        }

        for (int i = 0; i < items.Length; i++)
        {
            if (items[i].Kind != ItemKind.Block)
            {
                _flat[i] = true;
                continue;
            }

            BlockShape shape = blocks.Definition(_itemToBlock[i]).Shape;

            _flat[i] = shape is BlockShape.Cross or BlockShape.Foliage or BlockShape.GroundClutter
                or BlockShape.Door or BlockShape.Trapdoor or BlockShape.Ladder or BlockShape.Torch;
        }
    }

    private readonly BlockRegistry _blocks;

    public int Count => _items.Length;

    public ItemDefinition Definition(int item) => _items[item];

    /// <summary>Index předmětu, nebo <see cref="Nothing"/>.</summary>
    public int IndexOf(string id) => _byId.TryGetValue(id, out int index) ? index : Nothing;

    /// <summary>Předmět, který po sobě blok zanechá. <see cref="Nothing"/>, když žádný.</summary>
    public int ItemForBlock(ushort block) =>
        block < _blockToItem.Length ? _blockToItem[block] : Nothing;

    /// <summary>Blok, který předmět položí. <see cref="BlockRegistry.Air"/>, když žádný.</summary>
    public ushort BlockForItem(int item) =>
        item >= 0 && item < _itemToBlock.Length ? _itemToBlock[item] : BlockRegistry.Air;

    /// <summary>Vrstva textury pro ikonu předmětu.</summary>
    public int IconLayer(int item)
    {
        ItemDefinition definition = _items[item];

        // Modelovy blok muze mit vlastni inventarovou miniaturu. Jeho bezna textura je
        // rozlozeny UV atlas a ve slotu by se zobrazila jako zmet car misto stroje.
        if (!string.IsNullOrWhiteSpace(definition.IconTexture))
        {
            return _iconLayers[item];
        }

        // Blok se v inventáři pozná podle vrchní stěny. Je to ta, kterou hráč vidí
        // ve světě nejčastěji, takže se ikona shoduje s tím, co si pamatuje.
        return definition.Kind == ItemKind.Block
            ? _blocks.FaceLayer(BlockForItem(item), BlockFace.PosY)
            : _iconLayers[item];
    }

    internal bool TryGetBlockIcon(int item, out BlockItemIcon icon)
    {
        icon = default;
        if (item < 0 || item >= _items.Length) return false;

        ItemDefinition definition = _items[item];
        if (definition.Kind != ItemKind.Block || !string.IsNullOrWhiteSpace(definition.IconTexture))
        {
            return false;
        }

        ushort block = BlockForItem(item);
        BlockShape shape = _blocks.ShapeOf(block);
        if (shape is not (BlockShape.Cube or BlockShape.Stairs))
        {
            return false;
        }

        icon = new BlockItemIcon(
            shape == BlockShape.Stairs
                ? PieceMask.StairOccupancy(_blocks.DefaultPieces(block))
                : _blocks.DefaultPieces(block),
            _blocks.FaceLayer(block, BlockFace.PosY),
            _blocks.FaceLayer(block, BlockFace.PosX),
            _blocks.FaceLayer(block, BlockFace.PosZ));
        return true;
    }

    private int[] _iconLayers = [];

    /// <summary>
    /// Kreslí se předmět ve světě jako plochý obrázek místo krychličky?
    /// </summary>
    /// <remarks>
    /// <para><b>Krychle sedí jen na plné bloky.</b> Nástroj má ikonu, která je z větší
    /// části průhledná — krychle z krumpáče je pět stejných obrázků nalepených na hranách
    /// a nevypadá jako nic. Tráva a listí jsou na tom stejně: jejich textura je sprite
    /// stébel, ne povrch materiálu.</para>
    ///
    /// <para>Rozhoduje se při stavbě registru, ne při kreslení: je to vlastnost předmětu
    /// a v kreslení by to znamenalo sahat na registr bloků u každé krychličky.</para>
    /// </remarks>
    public bool IsFlat(int item) => item >= 0 && item < _flat.Length && _flat[item];

    private readonly bool[] _flat;

    /// <summary>Jména textur, která musí umět atlas. Volá se před stavbou atlasu.</summary>
    public IEnumerable<string> IconTextures() =>
        _items.Select(IconTextureOf)
            .Where(name => !string.IsNullOrEmpty(name));

    /// <summary>Doplní vrstvy ikon, jakmile je atlas hotový.</summary>
    public void ResolveIcons(Func<string, int> layerOf)
    {
        ArgumentNullException.ThrowIfNull(layerOf);

        _iconLayers = new int[_items.Length];

        for (int i = 0; i < _items.Length; i++)
        {
            string texture = IconTextureOf(_items[i]);
            _iconLayers[i] = string.IsNullOrEmpty(texture) ? 0 : layerOf(texture);
        }
    }

    /// <summary>Jméno obrázku pro slot; modelová UV mapa sem nikdy nesmí propadnout.</summary>
    private static string IconTextureOf(ItemDefinition definition) =>
        string.IsNullOrWhiteSpace(definition.IconTexture) ? definition.Texture : definition.IconTexture;

    /// <summary>
    /// Postaví registr: bloky převede na předměty a přidá ruční definice z adresáře.
    /// </summary>
    public static ItemRegistry Create(BlockRegistry blocks, string? itemDirectory)
    {
        ArgumentNullException.ThrowIfNull(blocks);

        IReadOnlyList<ContentFile> files = [];
        if (!string.IsNullOrEmpty(itemDirectory) && Directory.Exists(itemDirectory))
        {
            var info = new DirectoryInfo(Path.GetFullPath(itemDirectory));
            string root = info.Parent?.FullName
                ?? throw new InvalidDataException($"Adresář s předměty nemá nadřazený adresář: {itemDirectory}");
            var catalog = new ContentCatalog([new ContentSource("tesseris", root, LegacyFlat: true)]);
            files = catalog.GetFiles(info.Name);
        }

        return CreateFromFiles(blocks, files);
    }

    /// <summary>Postaví registr z bloků a všech zdrojů katalogu.</summary>
    public static ItemRegistry Create(BlockRegistry blocks, ContentCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        ArgumentNullException.ThrowIfNull(catalog);
        return CreateFromFiles(blocks, catalog.GetFiles("items"));
    }

    private static ItemRegistry CreateFromFiles(BlockRegistry blocks, IReadOnlyList<ContentFile> files)
    {
        ArgumentNullException.ThrowIfNull(blocks);

        List<ItemDefinition> items = [];

        for (ushort block = 1; block < blocks.Count; block++)
        {
            if (blocks.IsLiquid(block))
            {
                continue;
            }

            BlockDefinition definition = blocks.Definition(block);

            // Varianty existují jen jako větší nálezy ve světě. Po rozbití dají
            // několik základních kusů a nemají se objevit jako samostatný inventářový blok.
            if (!string.IsNullOrWhiteSpace(definition.DropItem))
            {
                continue;
            }

            items.Add(new ItemDefinition
            {
                Id = definition.Id,
                Name = ShortName(definition.Id),
                Kind = ItemKind.Block,
                Block = definition.Id,
                MaxStack = 64,

                // Hořlavost si nese blok: dřevo je palivo do pece a nemá kvůli tomu
                // existovat druhá definice v assets/items.
                BurnSeconds = definition.BurnSeconds,
                BurnResidue = definition.BurnResidue,
            });
        }

        var explicitFiles = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (ContentFile file in files)
        {
            string path = file.FullPath;
            ItemDefinition definition =
                JsonSerializer.Deserialize<ItemDefinition>(File.ReadAllText(path), JsonOptions)
                ?? throw new InvalidDataException($"Soubor {path} neobsahuje definici předmětu.");

            if (string.IsNullOrWhiteSpace(definition.Id))
            {
                if (file.Source.LegacyFlat)
                {
                    throw new InvalidDataException($"Soubor {path} nemá vyplněné 'id'.");
                }

                definition.Id = file.Id.ToString();
            }
            else
            {
                definition.Id = NormalizeDefinitionId(definition.Id, file);
            }

            if (!explicitFiles.TryAdd(definition.Id, path))
            {
                throw new InvalidDataException(
                    $"Předmět '{definition.Id}' je definovaný víckrát: " +
                    $"'{explicitFiles[definition.Id]}' a '{path}'.");
            }

            definition.Block = NormalizeReference(definition.Block, file.Source.Namespace);
            definition.BurnResidue = NormalizeReference(definition.BurnResidue, file.Source.Namespace);
            definition.Texture = NormalizeAssetReference(definition.Texture, file.Source);
            definition.IconTexture = NormalizeAssetReference(definition.IconTexture, file.Source);
            definition.Model = NormalizeModelReference(definition.Model, file.Source);

            // Nástroj ani kus výstroje se nestohuje. Kdyby se stohoval, slilo by se
            // opotřebení do jednoho čísla a zbytek by se ztratil.
            if (definition.Kind is ItemKind.Tool or ItemKind.Bucket or ItemKind.Armour)
            {
                definition.MaxStack = 1;
            }

            // SOUBOR PŘEBÍJÍ AUTOMATICKÝ PŘEDMĚT, NEPŘIDÁVÁ SE VEDLE NĚJ.
                //
                // Sazenice je blok i předmět s vlastnostmi navíc (hoří). Kdyby se přidala
                // podruhé, byly by v seznamu dva předměty téhož jména: automatický by uměl
                // položit blok, ruční by hořel, a `IndexOf` by vracel ten poslední. Padalo
                // by z listí něco, co se nedá zasadit.
            int existing = items.FindIndex(i => string.Equals(i.Id, definition.Id, StringComparison.Ordinal));

            if (existing >= 0)
            {
                items[existing] = definition;
                continue;
            }

            items.Add(definition);
        }

        items.Sort((left, right) => string.CompareOrdinal(left.Id, right.Id));
        return new ItemRegistry([.. items], blocks);
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
            throw new InvalidDataException($"Předmět v souboru {file.FullPath} má neplatné 'id' {value}.", exception);
        }

        if (!file.Source.LegacyFlat && !string.Equals(id.Namespace, file.Source.Namespace, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Předmět '{id}' v souboru {file.FullPath} nemůže definovat cizí namespace; " +
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

    private static string NormalizeAssetReference(string value, ContentSource source)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        int hash = value.IndexOf('#', StringComparison.Ordinal);
        string suffix = string.Empty;
        string reference = value;

        if (hash >= 0)
        {
            if (hash == 0 || hash != value.LastIndexOf('#')
                || !int.TryParse(value[(hash + 1)..], out int band) || band < 0)
            {
                throw new InvalidDataException(
                    $"Neplatný pruh textury '{value}'; očekává se přípona '#N'.");
            }

            reference = value[..hash];
            suffix = value[hash..];
        }

        if (source.LegacyFlat && !reference.Contains(':'))
        {
            return value;
        }

        try
        {
            return ResourceId.Resolve(reference, source.Namespace) + suffix;
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException($"Neplatný odkaz na texturu '{value}'.", exception);
        }
    }

    private static string NormalizeModelReference(string value, ContentSource source)
    {
        // Původní plochá složka páruje modely krátkým jménem. Její kontrakt
        // zůstává beze změny; namespace je povinný až u moderních content packů.
        if (source.LegacyFlat || string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        try
        {
            return ResourceId.Resolve(value, source.Namespace).ToString();
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException($"Neplatný odkaz na model '{value}'.", exception);
        }
    }

    /// <summary>Jméno bez jmenného prostoru, s podtržítky nahrazenými mezerou.</summary>
    private static string ShortName(string id)
    {
        int colon = id.LastIndexOf(':');
        return (colon >= 0 ? id[(colon + 1)..] : id).Replace('_', ' ');
    }
}
