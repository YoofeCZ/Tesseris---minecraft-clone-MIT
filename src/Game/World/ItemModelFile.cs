using System.Text.Json;
using OpenTK.Mathematics;
using Tesseris.Engine.Rendering;
using Tesseris.Game.Content;

namespace Tesseris.Game.World;

/// <summary>
/// Model předmětu z Blockbenche ve formátu Minecraft Java Block/Item.
/// </summary>
/// <remarks>
/// <para><b>Proč zrovna tenhle formát.</b> Blockbench ho umí vyexportovat, je to plochý
/// seznam kvádrů zarovnaných s osami a nic víc — žádný strom uzlů ani kvaterniony. Čtečka
/// je proto krátká a modelář si nemusí zvykat na nic vlastního.</para>
///
/// <para><b>Souřadnice jsou v šestnáctinách bloku</b> a smí přesahovat ven: sekera z ukázky
/// sahá od −3 do 15 vodorovně a od 0 do 24 svisle, tedy je vyšší než blok. Model se proto
/// na závěr zmenší podle své největší strany, aby se choval jako ostatní předměty.</para>
///
/// <para><b>Souřadnice v textuře jsou taky v šestnáctinách</b>, nezávisle na tom, jak je
/// textura velká; skutečné rozlišení říká <c>texture_size</c>. Na to se dá snadno naletět,
/// protože u výchozích 16×16 obojí splývá.</para>
///
/// <para><b>Textura se rozřeže na pruhy.</b> Atlas umí jen čtverce o hraně dlaždice, kdežto
/// tyhle textury jsou vysoké — u sekery 64×256. Každá stěna proto dostane číslo pruhu
/// a souřadnice uvnitř něj. Stěna, která by přes hranici pruhu přepadla, se zahodí:
/// nakreslila by kus úplně jiné části textury a hledalo by se to špatně.</para>
///
/// <para><b>Textura se jmenuje po modelu</b>, tedy <c>models/axe.json</c> hledá
/// <c>textures/axe.png</c>. Pole <c>textures</c> v souboru se ignoruje schválně: Blockbench
/// tam píše výchozí jméno „texture", což by se v naší jedné společné složce srazilo
/// s každým druhým modelem. Jedno pravidlo je lepší než jméno, které závisí na tom, jestli
/// si ho někdo v editoru přepsal.</para>
/// </remarks>
public sealed class ItemModelFile
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Kolik jednotek modelu připadá na blok. Dané formátem.</summary>
    private const float UnitsPerBlock = 16f;

    private ItemModelFile(string name, string[] layerNames, ItemShape shape)
    {
        Name = name;
        _layerNames = layerNames;
        Shape = shape;
    }

    private readonly string[] _layerNames;

    /// <summary>Jméno modelu podle souboru, bez přípony.</summary>
    public string Name { get; }

    /// <summary>Kolik pruhů model dohromady potřebuje, přes všechny své textury.</summary>
    public int Bands => _layerNames.Length;

    /// <summary>Hotové těleso. Vrstvy stěn jsou zatím čísla pruhů, ne vrstvy atlasu.</summary>
    public ItemShape Shape { get; private set; }

    /// <summary>
    /// Dosadí do stěn skutečné vrstvy atlasu. Volá se, až je atlas hotový.
    /// </summary>
    /// <remarks>
    /// Při čtení souboru se vrstvy atlasu ještě neznají — atlas vzniká až z jmen, která
    /// model teprve nahlásí. Do té doby si stěna nese číslo pruhu.
    /// </remarks>
    public void Resolve(Func<string, int> layerOf)
    {
        ArgumentNullException.ThrowIfNull(layerOf);

        Shape = Shape.WithLayers(slot => layerOf(_layerNames[Math.Clamp(slot, 0, _layerNames.Length - 1)]));
    }

    /// <summary>Jména vrstev, která musí atlas umět. Viz <see cref="TextureArray"/>.</summary>
    public IReadOnlyList<string> LayerNames() => _layerNames;

    /// <summary>
    /// Přečte všechny modely ze složky. Vadný soubor se přeskočí, hra kvůli němu nespadne.
    /// </summary>
    public static List<ItemModelFile> LoadAll(string directory, float half)
    {
        List<ItemModelFile> models = [];

        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            return models;
        }

        foreach (string path in Directory.GetFiles(directory, "*.json"))
        {
            ItemModelFile? model = TryLoad(path, half);

            if (model is not null)
            {
                models.Add(model);
            }
        }

        return models;
    }

    /// <summary>
    /// Přečte modely ze všech zdrojů katalogu. Jméno modelu je jeho kanonické
    /// <c>namespace:path</c> ID a stejný namespace dostanou i jeho relativní textury.
    /// </summary>
    public static List<ItemModelFile> LoadAll(
        ContentCatalog catalog,
        ContentAssetResolver assets,
        float half)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(assets);

        List<ItemModelFile> models = [];

        foreach (ContentFile file in catalog.GetFiles("models"))
        {
            ItemModelFile? model = TryLoad(file, assets, half);

            if (model is not null)
            {
                models.Add(model);
            }
        }

        return models;
    }

    /// <summary>Přečte jeden model, nebo vrátí <c>null</c>.</summary>
    public static ItemModelFile? TryLoad(string path, float half)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        return TryLoad(path, name, name, assets: null, half);
    }

    private static ItemModelFile? TryLoad(
        ContentFile file,
        ContentAssetResolver assets,
        float half) =>
        TryLoad(
            file.FullPath,
            file.Id.ToString(),
            file.Id.ToString(),
            assets,
            half,
            file.Source.Namespace);

    private static ItemModelFile? TryLoad(
        string path,
        string name,
        string defaultTexture,
        ContentAssetResolver? assets,
        float half,
        string? sourceNamespace = null)
    {
        try
        {
            Document? document = JsonSerializer.Deserialize<Document>(File.ReadAllText(path), Options);

            if (document?.Elements is not { Length: > 0 })
            {
                Engine.Core.Log.Warn($"Model {path} nemá jediný kvádr, přeskakuje se.");
                return null;
            }

            // Textury se čtou ze souboru, ne z `texture_size`. Model může mít víc textur
            // různých rozměrů — krumpáč má rukojeť ze 16×16 a hlavu z 32×32 — a jedno
            // číslo v hlavičce je popsat nedokáže.
            Dictionary<string, Sheet> sheets = ResolveSheets(
                document,
                defaultTexture,
                assets,
                sourceNamespace);

            var layers = new List<string>();

            foreach (Sheet sheet in sheets.Values)
            {
                sheet.FirstSlot = layers.Count;

                for (int band = 0; band < sheet.Bands; band++)
                {
                    layers.Add($"{sheet.File}#{band}");
                }
            }

            return new ItemModelFile(name, [.. layers], Build(document.Elements, sheets, half));
        }
        catch (Exception error) when (error is IOException or JsonException)
        {
            Engine.Core.Log.Warn($"Model {path} se nepodařilo přečíst: {error.Message}");
            return null;
        }
    }

    /// <summary>
    /// Jedna textura modelu: soubor, jeho skutečný rozměr a kde začínají její pruhy.
    /// </summary>
    private sealed class Sheet
    {
        public required string File { get; init; }

        public required int Width { get; init; }

        public required int Height { get; init; }

        /// <summary>Na kolik čtvercových pruhů se textura dělí.</summary>
        public int Bands => Math.Max(1, (Height + Width - 1) / Math.Max(1, Width));

        /// <summary>Index prvního pruhu ve společném seznamu vrstev modelu.</summary>
        public int FirstSlot { get; set; }
    }

    /// <summary>
    /// Přiřadí klíčům textur soubory a zjistí jejich skutečný rozměr.
    /// </summary>
    /// <remarks>
    /// <b>Hodnota v <c>textures</c> je jméno souboru</b> v <c>assets/textures</c>. Cesty
    /// typu <c>item/pick</c> se zkrátí na poslední kus, protože naše textury leží v jedné
    /// složce. Model bez seznamu textur si vezme soubor pojmenovaný po sobě.
    /// </remarks>
    private static Dictionary<string, Sheet> ResolveSheets(
        Document document,
        string modelName,
        ContentAssetResolver? assets,
        string? sourceNamespace)
    {
        Dictionary<string, Sheet> sheets = [];

        if (document.Textures is { Count: > 0 })
        {
            foreach ((string key, string value) in document.Textures)
            {
                // `particle` je barva odletujících kousků, ne textura geometrie. Založit
                // pro ni vrstvu by znamenalo tutéž texturu v atlasu dvakrát.
                if (string.IsNullOrEmpty(value) || key.Equals("particle", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string file;
                if (sourceNamespace is null)
                {
                    int slash = value.LastIndexOf('/');
                    file = slash >= 0 ? value[(slash + 1)..] : value;
                }
                else
                {
                    try
                    {
                        file = ResourceId.Resolve(value, sourceNamespace).ToString();
                    }
                    catch (FormatException exception)
                    {
                        throw new InvalidDataException(
                            $"Neplatný odkaz na texturu modelu '{value}'.",
                            exception);
                    }
                }

                (int width, int height) = MeasurePng(file, assets);

                sheets[key] = new Sheet { File = file, Width = width, Height = height };
            }
        }

        if (sheets.Count == 0)
        {
            (int width, int height) = MeasurePng(modelName, assets);
            sheets["0"] = new Sheet { File = modelName, Width = width, Height = height };
        }

        return sheets;
    }

    /// <summary>
    /// Přečte rozměr obrázku z hlavičky PNG. Chybějící soubor vyjde jako čtverec o hraně
    /// dlaždice, takže se model nakreslí bez textury místo aby se nenačetl.
    /// </summary>
    private static (int Width, int Height) MeasurePng(
        string file,
        ContentAssetResolver? assets)
    {
        try
        {
            string? path = assets is null
                ? Path.Combine(TextureArray.TextureFolder, file + ".png")
                : assets.ResolveTexturePath(file);

            if (path is null)
            {
                return (TextureArray.TileSize, TextureArray.TileSize);
            }

            using FileStream stream = File.OpenRead(path);

            Span<byte> header = stackalloc byte[24];

            if (stream.Read(header) != 24)
            {
                return (TextureArray.TileSize, TextureArray.TileSize);
            }

            int width = (header[16] << 24) | (header[17] << 16) | (header[18] << 8) | header[19];
            int height = (header[20] << 24) | (header[21] << 16) | (header[22] << 8) | header[23];

            return width > 0 && height > 0
                ? (width, height)
                : (TextureArray.TileSize, TextureArray.TileSize);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Engine.Core.Log.Warn($"Textura modelu '{file}' se nepodařilo změřit, kreslí se bez ní.");
            return (TextureArray.TileSize, TextureArray.TileSize);
        }
    }

    /// <summary>Složí z kvádrů těleso a zmenší ho tak, aby se vešlo do zadané poloviny.</summary>
    private static ItemShape Build(Element[] elements, Dictionary<string, Sheet> sheets, float half)
    {
        List<ItemShape.Face> faces = [];

        // Rohy se spočítají jednou, i s natočením — rozsah modelu se z nich musí odvodit
        // až POTOM. Natočený kvádr zabírá jinak než jeho zadání a podle nezatočeného
        // by model vylezl ze svých mezí.
        List<Vector3[]> corners = [];

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);

        foreach (Element element in elements)
        {
            Vector3[]? box = CornersOf(element);

            corners.Add(box!);

            if (box is null)
            {
                continue;
            }

            foreach (Vector3 point in box)
            {
                min = Vector3.ComponentMin(min, point);
                max = Vector3.ComponentMax(max, point);
            }
        }

        if (min.X > max.X)
        {
            return ItemShape.FromFaces([]);
        }

        // Střed modelu se posune do počátku, aby se točil kolem sebe a ne kolem rohu.
        Vector3 centre = (min + max) * 0.5f;

        // Zmenšení podle nejdelší strany. Bez toho by sekera vysoká 24 jednotek byla
        // ve světě jeden a půl bloku dlouhá.
        float longest = MathF.Max(max.X - min.X, MathF.Max(max.Y - min.Y, max.Z - min.Z));
        float scale = longest > 0f ? (half * 2f) / longest : 1f;

        for (int i = 0; i < elements.Length; i++)
        {
            if (corners[i] is not { } box)
            {
                continue;
            }

            var placed = new Vector3[8];

            for (int c = 0; c < 8; c++)
            {
                placed[c] = (box[c] - centre) * scale;
            }

            AddBox(faces, elements[i], placed, sheets);
        }

        return ItemShape.FromFaces(DropTouching(faces));
    }

    /// <summary>
    /// Zahodí dvojice stěn, které leží přesně na sobě.
    /// </summary>
    /// <remarks>
    /// <para><b>Tohle bylo to blikání textur v ruce.</b> Model nářadí je poskládaný z kvádrů,
    /// které na sebe dosedají — vršek rukojeti přesně na spodek hlavy. Dotyková plocha je
    /// pak dvakrát: jednou jako horní stěna spodního dílu, jednou jako dolní stěna horního.
    /// Obě se kreslí, obě mají jinou část textury a perou se o každý pixel, takže kresba
    /// mezi nimi při sebemenším pohybu přeskakuje.</para>
    ///
    /// <para><b>Obě jsou přitom neviditelné</b>, protože leží mezi dvěma tělesy. Zahodit je
    /// je tedy nejen oprava, ale i úspora — u meče to je osm stěn z osmačtyřiceti.</para>
    ///
    /// <para>Nechává se přitom stěna, která má protějšek jen v <b>jedné</b> rovině, ale ne
    /// na tomtéž místě: dva kvádry vedle sebe se dotýkají hranou, ne plochou, a tam
    /// zakrývat není co.</para>
    /// </remarks>
    private static ItemShape.Face[] DropTouching(List<ItemShape.Face> faces)
    {
        // Práh je zlomek nejmenší tloušťky, kterou plochým dílům dáváme, aby se nesmazaly
        // i stěny, které jsou jen blízko u sebe.
        const float Epsilon = 1e-5f;

        var doomed = new bool[faces.Count];

        for (int i = 0; i < faces.Count; i++)
        {
            if (doomed[i])
            {
                continue;
            }

            Vector3 centre = Centre(faces[i]);

            for (int j = i + 1; j < faces.Count; j++)
            {
                if (doomed[j] || (Centre(faces[j]) - centre).LengthSquared > Epsilon * Epsilon)
                {
                    continue;
                }

                doomed[i] = true;
                doomed[j] = true;
                break;
            }
        }

        List<ItemShape.Face> kept = [];

        for (int i = 0; i < faces.Count; i++)
        {
            if (!doomed[i])
            {
                kept.Add(faces[i]);
            }
        }

        return [.. kept];

        static Vector3 Centre(in ItemShape.Face face) =>
            (face.P0 + face.P1 + face.P2 + face.P3) * 0.25f;
    }

    /// <summary>
    /// Osm rohů kvádru v soustavě modelu, už i s natočením.
    /// </summary>
    /// <remarks>
    /// <para><b>Natočení je jediné, co formát dovoluje nad rámec kvádrů zarovnaných
    /// s osami</b> — a je omezené: jedna osa a úhel z pevné sady, prakticky násobky 22,5°.
    /// Přesto bez něj model vypadá zjevně špatně: rýč z ukázky má rukojeť i list otočené
    /// o −45° kolem osy Z, takže by mu bez toho trčely do strany.</para>
    ///
    /// <para>Pořadí rohů je podle bitů: 1 znamená horní mez v dané ose, tedy index
    /// <c>x + 2y + 4z</c>. Stěny si podle toho rohy vybírají.</para>
    /// </remarks>
    private static Vector3[]? CornersOf(Element element)
    {
        if (element.From is not { Length: 3 } || element.To is not { Length: 3 })
        {
            return null;
        }

        var from = new Vector3(element.From[0], element.From[1], element.From[2]);
        var to = new Vector3(element.To[0], element.To[1], element.To[2]);

        Vector3 a = Vector3.ComponentMin(from, to);
        Vector3 b = Vector3.ComponentMax(from, to);

        // PLOCHÝ KVÁDR DOSTANE VLAS TLOUŠŤKY.
        //
        // Modely nářadí mají díly zadané s nulovou tloušťkou — ostří, hroty, plotny. Přední
        // a zadní stěna takového dílu leží PŘESNĚ na sobě a každá nese jiný kus textury,
        // takže se o každý pixel perou a kresba mezi nimi přeskakuje. Hlásilo se to jako
        // „pořád to bliká ty textůry co jsou v ruce, ale jen ty tooly" — ruka ani blok
        // plochý díl nemají, proto blikaly jen nástroje.
        //
        // Rozestup je tak malý, že ho není poznat (setina jednotky, tedy zlomek milimetru
        // ve světě), ale hloubkovému testu úplně stačí. Alternativou by bylo jednu ze stěn
        // zahodit — jenže pak by byla jedna strana čepele natažená z opačné.
        const float MinThickness = 0.01f;

        for (int axis = 0; axis < 3; axis++)
        {
            if (b[axis] - a[axis] >= MinThickness)
            {
                continue;
            }

            float centre = (a[axis] + b[axis]) * 0.5f;

            a[axis] = centre - (MinThickness * 0.5f);
            b[axis] = centre + (MinThickness * 0.5f);
        }

        var corners = new Vector3[8];

        for (int i = 0; i < 8; i++)
        {
            corners[i] = new Vector3(
                (i & 1) == 0 ? a.X : b.X,
                (i & 2) == 0 ? a.Y : b.Y,
                (i & 4) == 0 ? a.Z : b.Z);
        }

        Rotation? rotation = element.Rotation;

        if (rotation is null || MathF.Abs(rotation.Angle) < 0.001f)
        {
            return corners;
        }

        Vector3 origin = rotation.Origin is { Length: 3 }
            ? new Vector3(rotation.Origin[0], rotation.Origin[1], rotation.Origin[2])
            : new Vector3(8f);

        float radians = MathHelper.DegreesToRadians(rotation.Angle);
        float cos = MathF.Cos(radians);
        float sin = MathF.Sin(radians);

        // Dorovnání velikosti. Natočený kvádr se u sousedů rozestoupí, takže si o to
        // formát umí říct — bez toho zůstane mezi díly škvíra.
        float stretch = rotation.Rescale ? 1f / MathF.Cos(MathF.Abs(radians)) : 1f;

        for (int i = 0; i < 8; i++)
        {
            Vector3 p = corners[i] - origin;

            corners[i] = origin + (element.Rotation!.Axis?.ToUpperInvariant() switch
            {
                "X" => new Vector3(p.X, ((p.Y * cos) - (p.Z * sin)) * stretch, ((p.Y * sin) + (p.Z * cos)) * stretch),
                "Z" => new Vector3(((p.X * cos) - (p.Y * sin)) * stretch, ((p.X * sin) + (p.Y * cos)) * stretch, p.Z),
                _ => new Vector3(((p.X * cos) + (p.Z * sin)) * stretch, p.Y, ((p.Z * cos) - (p.X * sin)) * stretch),
            });
        }

        return corners;
    }

    /// <summary>Šest stěn jednoho kvádru. Chybějící stěna v datech se prostě nekreslí.</summary>
    private static void AddBox(
        List<ItemShape.Face> faces, Element element, Vector3[] c, Dictionary<string, Sheet> sheets)
    {
        Faces? sides = element.Faces;

        if (sides is null)
        {
            return;
        }

        // Rohy podle bitů: x + 2y + 4z. Osy odpovídají minecraftí orientaci — sever je −Z,
        // východ +X, nahoru +Y.
        Add(sides.North, [c[1], c[0], c[2], c[3]], 2, false);
        Add(sides.South, [c[4], c[5], c[7], c[6]], 2, true);
        Add(sides.West, [c[0], c[4], c[6], c[2]], 0, false);
        Add(sides.East, [c[5], c[1], c[3], c[7]], 0, true);
        Add(sides.Up, [c[6], c[7], c[3], c[2]], 1, true);
        Add(sides.Down, [c[0], c[1], c[5], c[4]], 1, false);

        void Add(Face? face, Vector3[] points, int axis, bool positive)
        {
            if (face?.Uv is not { Length: 4 })
            {
                return;
            }

            if (!TryPick(sheets, face.Texture, out Sheet? sheet) || sheet is null)
            {
                return;
            }

            if (!TryMapUv(face.Uv, sheet, out Vector2 uv0, out Vector2 uv1, out int slot))
            {
                return;
            }

            float shade = FaceShading.ForAxis(axis, positive);

            faces.Add(new ItemShape.Face(
                points[0], points[1], points[2], points[3],
                new Vector2(uv0.X, uv1.Y), new Vector2(uv1.X, uv1.Y),
                new Vector2(uv1.X, uv0.Y), new Vector2(uv0.X, uv0.Y),
                shade,
                slot));
        }
    }

    /// <summary>
    /// Převede minecraftí souřadnice v textuře na pruh atlasu a souřadnice uvnitř něj.
    /// </summary>
    /// <remarks>
    /// Vrací <c>false</c>, když stěna přepadá přes hranici pruhu. Nakreslila by kus úplně
    /// jiné části textury a na modelu by to vypadalo jako chyba geometrie, ne jako chybějící
    /// podpora — proto radši nic.
    /// </remarks>
    /// <summary>Textura, na kterou se stěna odkazuje. Klíč je tvaru <c>#0</c>.</summary>
    private static bool TryPick(Dictionary<string, Sheet> sheets, string? reference, out Sheet? sheet)
    {
        string key = string.IsNullOrEmpty(reference) ? "0" : reference.TrimStart('#');

        // Stěna s neznámým odkazem se přeskočí. Nakreslit ji cizí texturou by vypadalo jako
        // chyba v modelu, kterou by modelář hledal v Blockbenchi.
        return sheets.TryGetValue(key, out sheet) || sheets.TryGetValue("0", out sheet);
    }

    private static bool TryMapUv(
        float[] uv, Sheet sheet, out Vector2 min, out Vector2 max, out int slot)
    {
        min = default;
        max = default;
        slot = sheet.FirstSlot;

        // Ze šestnáctin na pixely SKUTEČNÉ textury.
        float x0 = MathF.Min(uv[0], uv[2]) / UnitsPerBlock * sheet.Width;
        float x1 = MathF.Max(uv[0], uv[2]) / UnitsPerBlock * sheet.Width;
        float y0 = MathF.Min(uv[1], uv[3]) / UnitsPerBlock * sheet.Height;
        float y1 = MathF.Max(uv[1], uv[3]) / UnitsPerBlock * sheet.Height;

        // Pruh je čtverec o straně šířky textury.
        float side = Math.Max(1, sheet.Width);

        int band = (int)MathF.Floor(MathF.Max(0f, y0) / side);

        // Horní hrana se počítá o chlup níž, aby stěna končící přesně na hranici pruhu
        // nespadla do toho následujícího.
        if ((int)MathF.Floor(MathF.Max(0f, y1 - 0.001f) / side) != band || band >= sheet.Bands)
        {
            return false;
        }

        slot = sheet.FirstSlot + band;

        min = new Vector2(x0 / side, (y0 - (band * side)) / side);
        max = new Vector2(x1 / side, (y1 - (band * side)) / side);

        return true;
    }

    private sealed class Document
    {
        /// <summary>
        /// Klíč textury na jméno souboru v <c>assets/textures</c>.
        /// </summary>
        /// <remarks>
        /// Model jich může mít víc: krumpáč má rukojeť z jedné a hlavu z druhé, každou
        /// jinak velkou. Stěna říká, kterou chce, polem <c>texture</c> tvaru <c>#0</c>.
        /// </remarks>
        public Dictionary<string, string>? Textures { get; set; }

        public Element[]? Elements { get; set; }
    }

    private sealed class Element
    {
        public string? Name { get; set; }

        public float[]? From { get; set; }

        public float[]? To { get; set; }

        public Rotation? Rotation { get; set; }

        public Faces? Faces { get; set; }
    }

    /// <summary>Natočení kvádru kolem jedné osy. Jediné, co formát nad rámec kvádrů umí.</summary>
    private sealed class Rotation
    {
        public float Angle { get; set; }

        public string? Axis { get; set; }

        public float[]? Origin { get; set; }

        /// <summary>Dorovná velikost tak, aby natočený kvádr navazoval na sousedy.</summary>
        public bool Rescale { get; set; }
    }

    private sealed class Faces
    {
        public Face? North { get; set; }

        public Face? South { get; set; }

        public Face? East { get; set; }

        public Face? West { get; set; }

        public Face? Up { get; set; }

        public Face? Down { get; set; }
    }

    private sealed class Face
    {
        public float[]? Uv { get; set; }

        public string? Texture { get; set; }
    }
}
