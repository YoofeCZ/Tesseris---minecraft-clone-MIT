using System.Text.Json;
using Tesseris.Game.Content;
using Tesseris.Game.World;

namespace Tesseris.Game.Blocks;

/// <summary>
/// Načtené definice bloků a jejich očíslování.
///
/// Index 0 je vždy vzduch a nemá vlastní soubor. Ostatní bloky dostanou index podle
/// abecedně setříděného <see cref="BlockDefinition.Id"/> — pořadí souborů na disku ani
/// pořadí načítání tedy výsledek neovlivní. Na téhle stabilitě bude ve F6 stát mapování
/// id → index uložené v save souboru.
/// </summary>
public sealed class BlockRegistry
{
    /// <summary>Index vzduchu. Prázdný voxel má vždycky tuhle hodnotu.</summary>
    public const ushort Air = 0;

    private const int FacesPerBlock = 6;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly BlockDefinition[] _definitions;
    private readonly string[] _textureNames;
    private readonly int[] _faceLayers;   // [blok * 6 + stěna] → vrstva v texture array
    private readonly bool[] _opaque;
    private readonly bool[] _opaqueRender;
    private readonly bool[] _solid;
    private readonly bool[] _liquid;
    private readonly bool[] _aquatic;
    private readonly bool[] _containsWater;
    private readonly bool[] _cutout;
    private readonly byte[] _sunlightCost;
    private readonly byte[] _emission;
    private readonly float[] _overhang;
    private readonly BlockShape[] _shapes;
    private readonly byte[] _pieces;
    private readonly GroundClutterModel?[] _groundModels;
    private readonly HytaleBlockModel?[] _hytaleModels;
    private readonly IReadOnlyList<ItemShape.Face>?[] _hytaleFaces;
    private readonly ushort[][] _ground;
    private readonly Dictionary<string, ushort> _byId;

    private BlockRegistry(BlockDefinition[] definitions, string[] textureNames, int[] faceLayers)
    {
        _definitions = definitions;
        _textureNames = textureNames;
        _faceLayers = faceLayers;

        _opaque = new bool[definitions.Length];
        _opaqueRender = new bool[definitions.Length];
        _solid = new bool[definitions.Length];
        _liquid = new bool[definitions.Length];
        _aquatic = new bool[definitions.Length];
        _containsWater = new bool[definitions.Length];
        _cutout = new bool[definitions.Length];
        _sunlightCost = new byte[definitions.Length];
        _emission = new byte[definitions.Length];
        _overhang = new float[definitions.Length];
        _shapes = new BlockShape[definitions.Length];
        _pieces = new byte[definitions.Length];
        _groundModels = new GroundClutterModel?[definitions.Length];
        _hytaleModels = new HytaleBlockModel?[definitions.Length];
        _hytaleFaces = new IReadOnlyList<ItemShape.Face>?[definitions.Length];
        _ground = new ushort[definitions.Length][];
        _byId = new Dictionary<string, ushort>(definitions.Length, StringComparer.Ordinal);

        for (int i = 0; i < definitions.Length; i++)
        {
            _opaque[i] = definitions[i].Opaque;
            _opaqueRender[i] = definitions[i].Opaque;
            _solid[i] = definitions[i].Solid;
            _liquid[i] = definitions[i].Liquid;
            _aquatic[i] = definitions[i].Aquatic;
            _containsWater[i] = definitions[i].Liquid || definitions[i].Aquatic;
            _cutout[i] = definitions[i].Cutout;
            _emission[i] = (byte)Math.Min(definitions[i].Emission, (byte)15);
            _overhang[i] = definitions[i].Overhang;
            _shapes[i] = definitions[i].Shape;
            _pieces[i] = definitions[i].Pieces;
            _groundModels[i] = definitions[i].GroundGeometry;
            _hytaleModels[i] = definitions[i].HytaleGeometry;
            _hytaleFaces[i] = definitions[i].HytaleFaces;
            _ground[i] = [];
            _byId[definitions[i].Id] = (ushort)i;
            if (definitions[i].Id.StartsWith("tesseris:", StringComparison.Ordinal))
            {
                _byId["voxelity:" + definitions[i].Id["tesseris:".Length..]] = (ushort)i;
            }
        }

        // Vzduch nesmí nikdy zakrývat sousedy ani do sebe nechat narazit, ať už v souboru
        // stojí cokoli.
        _opaque[Air] = false;
        _opaqueRender[Air] = false;
        _solid[Air] = false;
        _aquatic[Air] = false;
        _containsWater[Air] = false;

        // ZAKRÝVÁNÍ A NEPRŮHLEDNOST JSOU DVĚ RŮZNÉ VĚCI. Do téhle chvíle je nesl jeden
        // příznak a stačilo to, dokud byl každý blok plná krychle.
        //
        // Tvar, který svůj objem nevyplní celý (tráva, kmen, listí), NEZAKRÝVÁ sousedy —
        // kolem něj je vidět skrz. Vynucuje se to tady, aby na tom nezáleželo, co má kdo
        // v JSONu: z bloku trávy, který by zakrýval, by byla díra v terénu pod ním.
        //
        // KRESLÍ SE ale pořád neprůhledně, když má plnou texturu. Kdyby se tahle informace
        // ztratila spolu se zakrýváním, skončil by kmen v míchaném průchodu, který nezapisuje
        // do hloubky — a strom by byl skrz naskrz průhledný.
        for (int i = 0; i < definitions.Length; i++)
        {
            if (_shapes[i] is not BlockShape.Cube || _pieces[i] != PieceMask.Full)
            {
                _opaque[i] = false;
            }
        }

        // KOLIK STUPŇŮ UBERE PŘÍMÉMU SLUNCI. Luanti tomu říká `sunlight_propagates`,
        // Minecraft „light opacity"; obojí je JINÁ věc než průhlednost.
        //
        // <para>Tohle je celé „stíny" v blokových hrách. Sluneční sloupec padá svisle
        // v plné síle 15 a každý blok, kterým projde, mu ubere svoje. Pod korunou stromu
        // proto vzniká stín, který u okraje koruny plynule světlá — a nestojí ani jeden
        // draw call navíc, protože se to spočítá jednou při meshování.</para>
        //
        // <para><b>Není to vypínač, ale cena, a na tom celé stojí.</b> První verze měla
        // listí jako tvrdou zábranu: pod korunou spadlo světlo rovnou na nulu a v lese
        // byly ČERNÉ díry s ostrými hranami po blocích. Tak to nevypadá ani v Luanti ani
        // v Minecraftu — tam listí ubere pár stupňů a světlo pod stromem jen zeslábne.</para>
        //
        // <para>Sklo a porost neberou nic; v Minetest Game si obojí `sunlight_propagates
        // = true` nastavuje výslovně. Kytka, která si sama pod sebou dělá stín, vypadá
        // jako chyba.</para>
        for (int i = 0; i < definitions.Length; i++)
        {
            _sunlightCost[i] = _opaque[i] ? SunlightBlocked
                : _shapes[i] is BlockShape.Foliage ? (byte)3
                : _containsWater[i] ? (byte)2
                : (byte)0;
        }

        _sunlightCost[Air] = 0;

        // Podklady se překládají až po naplnění _byId — seznam odkazuje na jiné bloky
        // a ty ještě v půlce první smyčky nemusí být zapsané.
        for (int i = 0; i < definitions.Length; i++)
        {
            string[] names = definitions[i].GroundBlocks;

            if (names.Length == 0)
            {
                continue;
            }

            List<ushort> resolved = [];

            foreach (string name in names)
            {
                // Blok, který v registry není, se tiše přeskočí. Jinak by jediný překlep
                // v definici shodil celou hru při startu.
                if (_byId.TryGetValue(name, out ushort index))
                {
                    resolved.Add(index);
                }
            }

            _ground[i] = [.. resolved];
        }
    }

    /// <summary>
    /// Smí <paramref name="block"/> stát na <paramref name="ground"/>?
    ///
    /// <para>Blok bez vyjmenovaných podkladů smí stát na čemkoli — to je většina světa.
    /// Rostlina je naopak vázaná na svou půdu: tráva roste z trávníku, chaluha z písku
    /// na dně. Vícepatrová rostlina se navíc smí opřít <b>sama o sebe</b>, aby stvol
    /// držel i ve druhém a třetím patře.</para>
    /// </summary>
    public bool CanStandOn(ushort block, ushort ground)
    {
        if (block >= _ground.Length)
        {
            return true;
        }

        ushort[] allowed = _ground[block];

        if (allowed.Length == 0)
        {
            return true;
        }

        return ground == block || Array.IndexOf(allowed, ground) >= 0;
    }

    /// <summary>Je blok vázaný na podklad? Takový blok se bez něj rozpadne.</summary>
    public bool NeedsGround(ushort block) => block < _ground.Length && _ground[block].Length > 0;

    /// <summary>Počet typů bloků včetně vzduchu.</summary>
    public int Count => _definitions.Length;

    /// <summary>Názvy textur v pořadí odpovídajícím vrstvám texture array.</summary>
    public IReadOnlyList<string> TextureNames => _textureNames;

    /// <summary>
    /// Tabulka neprůhlednosti indexovaná identifikátorem bloku.
    ///
    /// Vystaveno přímo kvůli mesheru: ten se na neprůhlednost ptá v nejteplejší smyčce
    /// milionkrát za chunk a volání metody s vlastní kontrolou mezí se tam projeví.
    /// </summary>
    public ReadOnlySpan<bool> OpacityTable => _opaque;

    /// <summary>
    /// Tabulka křížových tvarů indexovaná identifikátorem bloku. Ze stejného důvodu jako
    /// <see cref="OpacityTable"/> je vystavená přímo: mesher se na ni ptá v nejteplejší smyčce.
    /// </summary>
    public ReadOnlySpan<BlockShape> ShapeTable => _shapes;

    /// <summary>Tabulka kapalin. Mesher se na ni ptá kvůli tmavnutí hluboké vody.</summary>
    public ReadOnlySpan<bool> LiquidTable => _liquid;

    /// <summary>Tabulka vodních rostlin, které sdílejí voxel s vodou.</summary>
    public ReadOnlySpan<bool> AquaticTable => _aquatic;

    /// <summary>Tabulka voxelů obsahujících vodu: kapalina i vodní rostlina.</summary>
    public ReadOnlySpan<bool> WaterTable => _containsWater;

    /// <summary>Vlastní světlo bloků v rozsahu 0 až 15, indexované runtime ID bloku.</summary>
    public ReadOnlySpan<byte> EmissionTable => _emission;

    /// <summary>Načte všechny <c>*.json</c> z adresáře.</summary>
    public static BlockRegistry LoadFromDirectory(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"Adresář s definicemi bloků neexistuje: {directory}");
        }

        var info = new DirectoryInfo(Path.GetFullPath(directory));
        string root = info.Parent?.FullName
            ?? throw new InvalidDataException($"Adresář s definicemi bloků nemá nadřazený adresář: {directory}");
        var catalog = new ContentCatalog([new ContentSource("tesseris", root, LegacyFlat: true)]);
        return LoadFiles(catalog, catalog.GetFiles(info.Name));
    }

    /// <summary>Načte bloky ze všech zdrojů katalogu.</summary>
    public static BlockRegistry Load(ContentCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return LoadFiles(catalog, catalog.GetFiles("blocks"));
    }

    private static BlockRegistry LoadFiles(ContentCatalog catalog, IReadOnlyList<ContentFile> files)
    {
        List<BlockDefinition> loaded = [];
        foreach (ContentFile file in files)
        {
            string path = file.FullPath;
            BlockDefinition definition = JsonSerializer.Deserialize<BlockDefinition>(File.ReadAllText(path), JsonOptions)
                ?? throw new InvalidDataException($"Soubor {path} neobsahuje definici bloku.");

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

            definition.Texture = NormalizeAssetReference(definition.Texture, file.Source);
            definition.Faces = definition.Faces.ToDictionary(
                pair => pair.Key,
                pair => NormalizeAssetReference(pair.Value, file.Source),
                StringComparer.Ordinal);
            definition.DropItem = NormalizeReference(definition.DropItem, file.Source.Namespace);
            definition.BurnResidue = NormalizeReference(definition.BurnResidue, file.Source.Namespace);
            definition.GroundBlocks = [.. definition.GroundBlocks
                .Select(id => NormalizeReference(id, file.Source.Namespace))];

            if (definition.Shape == BlockShape.GroundClutter)
            {
                if (string.IsNullOrWhiteSpace(definition.Model))
                {
                    throw new InvalidDataException(
                        $"Zemní nález {definition.Id} v souboru {path} nemá vyplněný 'model'.");
                }

                string modelPath = catalog.ResolvePath(
                    file.Source, "models", "ground", definition.Model + ".json");

                definition.GroundGeometry = GroundClutterModel.Load(modelPath);
            }

            if (definition.Shape == BlockShape.HytaleModel)
            {
                if (string.IsNullOrWhiteSpace(definition.Model))
                {
                    throw new InvalidDataException($"Modelovy blok {definition.Id} nema vyplnene 'model'.");
                }

                string modelPath = catalog.ResolvePath(
                    file.Source, "models", definition.Model + ".blockymodel");
                definition.HytaleGeometry = HytaleBlockModel.TryLoad(modelPath)
                    ?? throw new InvalidDataException($"Modelovy blok {definition.Id} nelze nacist z {modelPath}.");
                definition.HytaleFaces = definition.HytaleGeometry.BuildFaces(
                    definition.ModelTextureWidth, definition.ModelTextureHeight);
            }

            loaded.Add(definition);
        }

        return Create(loaded);
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
            throw new InvalidDataException($"Blok v souboru {file.FullPath} má neplatné 'id' {value}.", exception);
        }

        if (!file.Source.LegacyFlat && !string.Equals(id.Namespace, file.Source.Namespace, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Blok '{id}' v souboru {file.FullPath} nemůže definovat cizí namespace; " +
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

    /// <summary>
    /// Sestaví registry z hotových definic. Oddělené od načítání z disku, aby šlo testovat
    /// bez souborů.
    /// </summary>
    public static BlockRegistry Create(IEnumerable<BlockDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);

        var air = new BlockDefinition
        {
            Id = "tesseris:air",
            Material = BlockMaterial.None,
            Chiselable = false,
            Opaque = false,
        };

        BlockDefinition[] sorted = [.. definitions.OrderBy(d => d.Id, StringComparer.Ordinal)];

        // The content convention is public: every block ending in _stairs receives the
        // stateful stair shape, so orientation and corners work for modded stairs too.
        foreach (BlockDefinition definition in sorted)
        {
            if (definition.Shape == BlockShape.Cube
                && definition.Id.EndsWith("_stairs", StringComparison.Ordinal))
            {
                definition.Shape = BlockShape.Stairs;
            }
        }

        // Runtime index je ushort a několik horkých smyček ho záměrně používá přímo.
        // Hodnota 0 patří vzduchu a Count musí zůstat pod 65 536, aby se ushortová smyčka
        // po posledním bloku nepřetočila zpátky na nulu.
        if (sorted.Length > ushort.MaxValue - 1)
        {
            throw new InvalidDataException(
                $"Registry obsahuje {sorted.Length} bloků; maximum je {ushort.MaxValue - 1} plus vzduch.");
        }

        var duplicates = sorted.GroupBy(d => d.Id, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicates is not null)
        {
            throw new InvalidDataException($"Blok '{duplicates.Key}' je definovaný víckrát.");
        }

        BlockDefinition[] all = [air, .. sorted];

        // Vrstvy textur se číslují taky abecedně, ze stejného důvodu jako bloky.
        string[] textureNames = [.. all
            .SelectMany(d => d.Faces.Values.Append(d.Texture))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)];

        var layerByName = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < textureNames.Length; i++)
        {
            layerByName[textureNames[i]] = i;
        }

        int[] faceLayers = new int[all.Length * FacesPerBlock];
        for (int block = 0; block < all.Length; block++)
        {
            for (int face = 0; face < FacesPerBlock; face++)
            {
                string name = ResolveTextureName(all[block], (BlockFace)face);
                faceLayers[(block * FacesPerBlock) + face] = name.Length == 0 ? 0 : layerByName[name];
            }
        }

        return new BlockRegistry(all, textureNames, faceLayers);
    }

    public bool IsAir(ushort block) => block == Air;

    /// <summary>Zakrývá blok stěny sousedů?</summary>
    public bool IsOpaque(ushort block) => block < _opaque.Length && _opaque[block];

    /// <summary>
    /// Kreslí se blok v neprůhledném průchodu?
    ///
    /// <para>Liší se od <see cref="IsOpaque"/> u tvarů, které svůj objem nevyplní celý:
    /// kmen sousedy nezakrývá, ale sám je z plné textury a do míchaného průchodu nepatří.</para>
    /// </summary>
    public bool IsOpaqueRender(ushort block) => block < _opaqueRender.Length && _opaqueRender[block];

    /// <summary>
    /// Dá se do bloku narazit? Trávou a kytkami se dá projít, i když tam něco je.
    /// </summary>
    public bool IsSolid(ushort block) => block < _solid.Length && _solid[block];

    /// <summary>
    /// Je blok průchozí vegetace, kterou může běžný stavební blok přímo nahradit?
    /// </summary>
    /// <remarks>
    /// Samotné <see cref="IsSolid"/> nestačí: voda je také nesolidní, ale není vegetace.
    /// Vazba na podklad odlišuje trávu, květiny, sazenice a vodní rostliny; pevný kaktus
    /// se za nahraditelnou vegetaci nepovažuje.
    /// </remarks>
    public bool IsReplaceableVegetation(ushort block) =>
        NeedsGround(block) && !IsSolid(block) && ShapeOf(block) == BlockShape.Cross;

    /// <summary>Nízký klacík, kámen nebo pazourek, který smí položený blok nahradit.</summary>
    public bool IsReplaceableGroundClutter(ushort block) =>
        NeedsGround(block) && !IsSolid(block) && ShapeOf(block) == BlockShape.GroundClutter;

    /// <summary>
    /// Smí být obsah voxelu při pokládání nahrazen bez předchozího vytěžení?
    /// </summary>
    public bool IsReplaceable(ushort block) =>
        IsAir(block) || IsLiquid(block) || IsReplaceableVegetation(block)
        || IsReplaceableGroundClutter(block);

    /// <summary>Je blok kapalina? Rozhoduje o plavání a o zaplavování prostoru pod hladinou.</summary>
    public bool IsLiquid(ushort block) => block < _liquid.Length && _liquid[block];

    /// <summary>Je blok vodní rostlina, která vždy stojí uvnitř vody?</summary>
    public bool IsAquatic(ushort block) => block < _aquatic.Length && _aquatic[block];

    /// <summary>Obsahuje voxel tohoto typu vodu, i když jeho hlavní blok je rostlina?</summary>
    public bool ContainsWater(ushort block) =>
        block < _containsWater.Length && _containsWater[block];

    /// <summary>
    /// Kreslí se blok výřezem místo míchání? Viz <see cref="BlockDefinition.Cutout"/>.
    /// </summary>
    public bool IsCutout(ushort block) => block < _cutout.Length && _cutout[block];

    /// <summary>Cena, kterou tenhle blok znamená pro sluneční sloupec.</summary>
    public const byte SunlightBlocked = 15;

    /// <summary>
    /// O kolik stupňů ubere tenhle blok přímému slunci, které jím prochází svisle dolů.
    /// Luanti <c>sunlight_propagates</c>, Minecraft „light opacity".
    /// </summary>
    /// <remarks>
    /// Není to totéž co <see cref="IsOpaque"/>: listí je průhledné, a přesto slunce tlumí,
    /// zatímco sklo je taky průhledné a nebere nic. Odsud pochází stín pod stromem.
    /// </remarks>
    public byte SunlightCost(ushort block) =>
        block < _sunlightCost.Length ? _sunlightCost[block] : SunlightBlocked;

    /// <summary>Zastaví tenhle blok přímé slunce, byť jen částečně?</summary>
    public bool BlocksSunlight(ushort block) => SunlightCost(block) > 0;

    /// <summary>O kolik bloku mesh přesahuje ven. Viz <see cref="BlockDefinition.Overhang"/>.</summary>
    public float OverhangOf(ushort block) => block < _overhang.Length ? _overhang[block] : 0f;

    public ReadOnlySpan<float> OverhangTable => _overhang;

    /// <summary>Tabulka výřezu pro meshing. Indexuje se blokem.</summary>
    public ReadOnlySpan<bool> CutoutTable => _cutout;

    /// <summary>Tvar geometrie bloku.</summary>
    public BlockShape ShapeOf(ushort block) => block < _shapes.Length ? _shapes[block] : BlockShape.Cube;

    public byte DefaultPieces(ushort block) => block < _pieces.Length ? _pieces[block] : PieceMask.Full;

    public GroundClutterModel? GroundModelOf(ushort block) =>
        block < _groundModels.Length ? _groundModels[block] : null;

    public HytaleBlockModel? HytaleModelOf(ushort block) =>
        block < _hytaleModels.Length ? _hytaleModels[block] : null;

    public IReadOnlyList<ItemShape.Face>? HytaleFacesOf(ushort block) =>
        block < _hytaleFaces.Length ? _hytaleFaces[block] : null;

    /// <summary>Vrstva texture array pro danou stěnu bloku.</summary>
    public int FaceLayer(ushort block, BlockFace face) =>
        _faceLayers[(block * FacesPerBlock) + (int)face];

    public BlockDefinition Definition(ushort block) => _definitions[block];

    /// <summary>
    /// Najde index bloku podle textového id, nebo vrátí false.
    ///
    /// <para>Používá načítání uloženého světa: blok, který v registry už není, nesmí
    /// shodit hru — jinak by jediný odebraný typ znepřístupnil celý svět.</para>
    /// </summary>
    public bool TryIndexOf(string id, out ushort index) => _byId.TryGetValue(id, out index);

    /// <summary>Najde index bloku podle textového id. Vyhodí výjimku, když neexistuje.</summary>
    public ushort IndexOf(string id)
    {
        if (!_byId.TryGetValue(id, out ushort index))
        {
            throw new KeyNotFoundException($"Blok '{id}' není v registry.");
        }

        return index;
    }

    /// <summary>
    /// Vybere název textury pro stěnu. Pořadí hledání: konkrétní stěna, pak <c>side</c>
    /// u svislých stěn, nakonec výchozí textura bloku.
    /// </summary>
    private static string ResolveTextureName(BlockDefinition definition, BlockFace face)
    {
        string key = face switch
        {
            BlockFace.PosY => "top",
            BlockFace.NegY => "bottom",
            BlockFace.NegX => "west",
            BlockFace.PosX => "east",
            BlockFace.NegZ => "north",
            BlockFace.PosZ => "south",
            _ => string.Empty,
        };

        if (definition.Faces.TryGetValue(key, out string? explicitName) && !string.IsNullOrWhiteSpace(explicitName))
        {
            return explicitName;
        }

        bool horizontal = face is BlockFace.NegX or BlockFace.PosX or BlockFace.NegZ or BlockFace.PosZ;
        if (horizontal && definition.Faces.TryGetValue("side", out string? sideName) && !string.IsNullOrWhiteSpace(sideName))
        {
            return sideName;
        }

        return definition.Texture ?? string.Empty;
    }
}
