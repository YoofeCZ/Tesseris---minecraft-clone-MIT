using OpenTK.Mathematics;
using StbImageSharp;

using Tesseris.Engine.Rendering.Vulkan;

namespace Tesseris.Engine.Rendering;

/// <summary>
/// Pole textur, kde každý název dostane vlastní vrstvu. Index vrstvy cestuje do shaderu
/// jako atribut vrcholu, takže celý chunk se vykreslí bez přepínání textur.
///
/// <para><b>Obsah se bere přednostně ze souboru</b> <c>assets/textures/{název}.png</c>.
/// Když soubor chybí, dlaždice se vygeneruje kódem a rovnou se na disk <b>zapíše</b> —
/// takže po prvním spuštění je celá sada k dispozici jako obrázky, které jde otevřít
/// v editoru, přemalovat a příště se načtou.</para>
///
/// <para>Generátory v kódu tím nezanikly, jen se z nich stala <b>výchozí sada</b>. Je to
/// tentýž postup, jakým fungují resource packy v blokových hrách; samotné načítání textur
/// podle jména je technika, ne cizí obsah.</para>
/// </summary>
public sealed class TextureArray : IDisposable
{
    /// <summary>
    /// Hrana jedné dlaždice v texelech.
    ///
    /// <para><b>Zvýšeno ze 16 na 64.</b> Šestnáct je rozlišení, ve kterém se textura kreslí
    /// ručně — jenže do něj se nevejde nic, co by mělo strukturu. Při pokusu vygenerovat
    /// textury difuzním modelem se to ukázalo naplno: zmenšení z 1024 na 16 je faktor 64,
    /// takže se praskliny v kameni i léta ve dřevě zprůměrovaly do jedné ploché barvy.</para>
    ///
    /// <para>Cena je paměť: dlaždice má šestnáctkrát víc texelů, takže atlas o 41 vrstvách
    /// roste ze 168 kB na 2,7 MB (plus mipmapy). Vedle chunkových meshů je to zanedbatelné.</para>
    /// </summary>
    // Zdrojové obrázky mohou být větší, ale GPU atlas je převzorkuje. Držet 512² pro
    // každou z desítek voxelových vrstev spotřebovalo přes 120 MB a velký staging buffer;
    // 128² zachová detail modelů a je 16× menší než 512².
    public const int TileSize = 128;

    /// <summary>
    /// V jakém rozlišení kreslí <b>generátory v kódu</b>.
    ///
    /// <para>Zůstalo na šestnácti schválně. Jejich rozměry jsou napevno — okraj trávy měří
    /// čtyři texely, pásy pískovce se střídají po čtyřech, ruční kresby rostlin mají šestnáct
    /// řádků. Ve větší dlaždici by z toho byly nitky přes celou plochu a všechny by se musely
    /// přepsat.</para>
    ///
    /// <para>Výsledek se proto zvětší nejbližším sousedem, čímž zůstane vzhled přesně takový,
    /// jaký byl. Obrázky ze souborů tímhle neprocházejí — ty jsou rovnou v plném rozlišení,
    /// takže kdo chce jemnější texturu, nakreslí ji jako PNG.</para>
    /// </summary>
    public const int ArtSize = 16;

    /// <summary>
    /// Kde se hledají a kam se zapisují obrázky dlaždic.
    ///
    /// <para>Odvozeno od <see cref="AppContext.BaseDirectory"/>, ne od pracovního adresáře:
    /// stejně to dělá i registr bloků, protože složka <c>assets</c> se při překladu kopíruje
    /// vedle binárky. Podle pracovního adresáře by se hra chovala jinak podle toho, odkud
    /// je spuštěná.</para>
    /// </summary>
    public static string TextureFolder { get; } =
        Path.Combine(AppContext.BaseDirectory, "assets", "textures");

    private readonly VulkanTexture _texture;
    private readonly Vector4[] _ink;

    /// <summary>Jakou část dlaždice zabírá skin modelu. Bloky a ikony mají vždy (1, 1).</summary>
    private readonly Vector2[] _skinScales;

    /// <summary>Alfa každé vrstvy zmenšená na <see cref="ArtSize"/>. Viz <see cref="Silhouette"/>.</summary>
    private readonly byte[][] _silhouettes;

    public TextureArray(VulkanContext context, IReadOnlyList<string> layerNames)
        : this(context, layerNames, ResolveLegacyTexturePath, cacheNamespacedFallbacks: true, skinLayers: null)
    {
    }

    /// <summary>
    /// Creates the texture array with an injected path resolver. Namespaced missing textures
    /// are generated in memory only; bare legacy textures retain the generated PNG cache.
    /// </summary>
    /// <param name="skinLayers">
    /// Vrstvy, které jsou UV mapou modelu, ne dlaždicí bloku — smí být nečtvercové.
    /// Viz <see cref="TryLoadPng"/>.
    /// </param>
    public TextureArray(
        VulkanContext context,
        IReadOnlyList<string> layerNames,
        Func<string, string?> resolveTexturePath,
        IReadOnlySet<string>? skinLayers = null)
        : this(context, layerNames, resolveTexturePath, cacheNamespacedFallbacks: false, skinLayers)
    {
    }

    private readonly VulkanContext _context;

    private TextureArray(
        VulkanContext context,
        IReadOnlyList<string> layerNames,
        Func<string, string?> resolveTexturePath,
        bool cacheNamespacedFallbacks,
        IReadOnlySet<string>? skinLayers)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        ArgumentNullException.ThrowIfNull(layerNames);
        ArgumentNullException.ThrowIfNull(resolveTexturePath);

        // Prázdné pole vrstev by ovladač odmítl, takže se vždycky vyrobí aspoň jedna.
        LayerCount = Math.Max(1, layerNames.Count);

        var layers = new byte[LayerCount][];
        _ink = new Vector4[LayerCount];
        _silhouettes = new byte[LayerCount][];
        _skinScales = new Vector2[LayerCount];

        for (int layer = 0; layer < LayerCount; layer++)
        {
            string name = layer < layerNames.Count ? layerNames[layer] : string.Empty;

            byte[] pixels = LoadOrCreateTile(
                name, resolveTexturePath, cacheNamespacedFallbacks, skinLayers?.Contains(name) == true,
                out Vector2 skinScale);
            _skinScales[layer] = skinScale;

            // Rozsah kresby se musí změřit PŘED rozléváním barvy do průhledných texelů —
            // to alfu nemění, ale kdyby se to jednou změnilo, měřilo by se rozlité okolí
            // místo kresby.
            _ink[layer] = MeasureInk(pixels);

            // Obrys taky před rozléváním, a ze stejného důvodu.
            _silhouettes[layer] = MeasureSilhouette(pixels);

            BleedColourIntoTransparent(pixels);

            layers[layer] = pixels;
        }

        // Opakování je nutné: greedy meshing dělá obdélníky přes víc bloků a UV jdou až
        // po počet sloučených bloků.
        _texture = VulkanTexture.CreateArray(_context, TileSize, layers, mipmaps: true, repeat: true);
    }

    /// <summary>
    /// Načte vrstvy jako syrové RGBA pixely, každou <see cref="TileSize"/>², bez zakládání
    /// textury na grafice.
    ///
    /// <para>Existuje kvůli kódu, který si texturu nahrává sám. Načítání je záměrně sdílené
    /// s konstruktorem — kdyby si každá cesta načítala po svém, rozešly by se v tom, co dělá
    /// s průhledností a s chybějícími soubory, a projevilo by se to až rozdílným obrazem.</para>
    /// </summary>
    public static byte[][] LoadLayerPixels(
        IReadOnlyList<string> layerNames,
        Func<string, string?> resolveTexturePath,
        bool cacheNamespacedFallbacks = false)
    {
        ArgumentNullException.ThrowIfNull(layerNames);
        ArgumentNullException.ThrowIfNull(resolveTexturePath);

        var layers = new byte[Math.Max(1, layerNames.Count)][];
        for (int layer = 0; layer < layers.Length; layer++)
        {
            string name = layer < layerNames.Count ? layerNames[layer] : string.Empty;
            byte[] pixels = LoadOrCreateTile(
                name, resolveTexturePath, cacheNamespacedFallbacks, isSkin: false, out _);

            BleedColourIntoTransparent(pixels);
            layers[layer] = pixels;
        }

        return layers;
    }

    public int LayerCount { get; }

    /// <summary>
    /// Kde v dlaždici je vůbec něco nakresleného: <c>x, y</c> je levý horní roh,
    /// <c>z, w</c> pravý dolní, obojí v UV od 0 do 1.
    ///
    /// <para><b>Slouží obrysu.</b> Rostlina je dvojice ploch přes celou úhlopříčku bloku,
    /// ale nakreslená je z ní jen kytka uprostřed — rámeček kolem celé plochy proto
    /// vypadal, jako by hráč mířil na kostku. S tímhle se obrys stáhne přesně na to,
    /// co je v textuře vidět.</para>
    ///
    /// <para>Prázdná dlaždice vrátí celý rozsah, aby se obrys nescvrkl na nic.</para>
    /// </summary>
    /// <summary>
    /// Jakou část dlaždice zabírá kresba skinu — model si tím musí přenásobit UV.
    /// Bloky, ikony i skiny zvětšené celou dlaždicí vracejí (1, 1).
    /// </summary>
    public Vector2 SkinScale(int layer) =>
        (uint)layer < (uint)_skinScales.Length && _skinScales[layer] != Vector2.Zero
            ? _skinScales[layer]
            : Vector2.One;

    public Vector4 InkBounds(int layer) =>
        (uint)layer < (uint)_ink.Length ? _ink[layer] : new Vector4(0f, 0f, 1f, 1f);

    /// <summary>
    /// Obrys kresby: <see cref="ArtSize"/>×<see cref="ArtSize"/> hodnot, kde nenula znamená
    /// „tady něco je". Řádek 0 je nahoře, stejně jako v textuře.
    /// </summary>
    /// <remarks>
    /// <para><b>K čemu to je.</b> Předmět ve světě není obrázek, ale těleso: meč, ingot ani
    /// klacek nejsou plachta. Vytáhnout z ploché ikony hloubku jde jedině tak, že se ví,
    /// které texely kresba zabírá — a to se musí vědět na procesoru, protože z hotové
    /// textury na grafické kartě se to nedá přečíst.</para>
    ///
    /// <para><b>Proč zmenšené.</b> Generované dlaždice jsou stejně nakreslené v šestnácti
    /// texelech a jen se zvětšují nejbližším sousedem, takže se zmenšením nic neztratí.
    /// U kreseb ze souborů je to zhrubnutí, ale těleso z předmětu má mít hrubé hrany —
    /// dvaašedesát tisíc stěn na jeden klacek by nikdo nechtěl.</para>
    /// </remarks>
    public ReadOnlySpan<byte> Silhouette(int layer) =>
        (uint)layer < (uint)_silhouettes.Length ? _silhouettes[layer] : ReadOnlySpan<byte>.Empty;

    /// <summary>Textura pro zápis do descriptor setu.</summary>
    public VulkanTexture Texture => _texture;

    public void Dispose() => _texture.Dispose();

    /// <summary>
    /// Zmenší alfu dlaždice na <see cref="ArtSize"/> a udělá z ní masku „tady něco je".
    /// </summary>
    /// <remarks>
    /// Bere se <b>nejvyšší</b> alfa z každé skupiny texelů, ne průměr. Průměr by z tenkého
    /// stébla nebo z čepele meče udělal poloprůhlednou kaši a pod prahem by z obrysu zmizely
    /// právě ty úzké části, kvůli kterým má předmět tvar.
    /// </remarks>
    private static byte[] MeasureSilhouette(byte[] rgba)
    {
        const byte Threshold = 128;

        int scale = TileSize / ArtSize;
        var mask = new byte[ArtSize * ArtSize];

        for (int y = 0; y < ArtSize; y++)
        {
            for (int x = 0; x < ArtSize; x++)
            {
                byte best = 0;

                for (int sy = 0; sy < scale; sy++)
                {
                    for (int sx = 0; sx < scale; sx++)
                    {
                        int index = ((((y * scale) + sy) * TileSize) + (x * scale) + sx) * 4;
                        best = Math.Max(best, rgba[index + 3]);
                    }
                }

                mask[(y * ArtSize) + x] = best >= Threshold ? (byte)1 : (byte)0;
            }
        }

        return mask;
    }

    /// <summary>
    /// Změří, kterou část dlaždice zabírá kresba, tedy texely s nenulovou alfou.
    /// </summary>
    private static Vector4 MeasureInk(byte[] rgba)
    {
        // Práh je stejný jako u výřezového průchodu: co shader zahodí, to ani nekreslí,
        // takže to nemá být uvnitř obrysu.
        const byte Threshold = 128;

        int minX = TileSize;
        int minY = TileSize;
        int maxX = -1;
        int maxY = -1;

        for (int y = 0; y < TileSize; y++)
        {
            for (int x = 0; x < TileSize; x++)
            {
                if (rgba[(((y * TileSize) + x) * 4) + 3] < Threshold)
                {
                    continue;
                }

                if (x < minX) { minX = x; }
                if (y < minY) { minY = y; }
                if (x > maxX) { maxX = x; }
                if (y > maxY) { maxY = y; }
            }
        }

        // Prázdná dlaždice: vrátí se celý rozsah, ať se obrys nescvrkne na nic.
        if (maxX < 0)
        {
            return new Vector4(0f, 0f, 1f, 1f);
        }

        return new Vector4(
            minX / (float)TileSize,
            minY / (float)TileSize,
            (maxX + 1) / (float)TileSize,
            (maxY + 1) / (float)TileSize);
    }

    /// <summary>
    /// Obsah dlaždice ze souboru, nebo vygenerovaný a na disk uložený.
    ///
    /// <para><b>Selhání se nikdy nesmí projevit pádem.</b> Chybějící složka, zamčený soubor
    /// i rozbitý obrázek skončí u generátoru v kódu — hra se spustí vždycky. Textura je
    /// ozdoba, ne podmínka běhu.</para>
    /// </summary>
    private static byte[] LoadOrCreateTile(
        string name,
        Func<string, string?> resolveTexturePath,
        bool cacheNamespacedFallbacks,
        bool isSkin,
        out Vector2 skinScale)
    {
        skinScale = Vector2.One;
        byte[] pixels = new byte[TileSize * TileSize * 4];

        // VYSOKÁ TEXTURA SE ROZŘEŽE NA PRUHY.
        //
        // Modely z Blockbenche mají texturu, do které se skládají stěny všech kvádrů vedle
        // sebe — u sekery je to 64×256. Atlas umí jen čtverce o hraně dlaždice, takže se
        // takový obrázek přihlásí jako několik vrstev se jménem `soubor#pruh` a každá si
        // vezme svých 64 řádků. Je to levnější než druhý atlas s jinou geometrií.
        int hash = name.IndexOf('#', StringComparison.Ordinal);

        if (hash > 0 && int.TryParse(name[(hash + 1)..], out int band))
        {
            string? bandPath = resolveTexturePath(name[..hash]);
            if (bandPath is null || !TryLoadBand(bandPath, band, pixels))
            {
                DrawGenerated(name, pixels);
            }

            return pixels;
        }

        if (string.IsNullOrEmpty(name))
        {
            DrawGenerated(name, pixels);
            return pixels;
        }

        string? path = resolveTexturePath(name);

        if (path is not null && File.Exists(path) && TryLoadPng(path, pixels, isSkin, out skinScale))
        {
            return pixels;
        }

        skinScale = Vector2.One;

        DrawGenerated(name, pixels);

        // NÁHRADNÍ KRESBA NIKDY NEPŘEPÍŠE EXISTUJÍCÍ SOUBOR.
        //
        // Cache má vyrobit chybějící obrázek, ne zahodit hotový. Když se skutečná textura
        // z jakéhokoli důvodu nenačte — špatný poměr stran, zamčený soubor, jiný formát —
        // je to důvod k opravě načítání, ne k přepsání originálu.
        //
        // Přesně tohle sežralo 44 skinů z Mobs Redo: nečtvercový obrázek se odmítl, vznikla
        // fialová dlaždice a ta se uložila na jeho místo. Původní skin byl pryč a chyba
        // pak přežila i opravu načítání, protože na disku už žádná pravda nezůstala.
        //
        // A namespaced reference belongs to a content pack. A generated missing-texture
        // fallback must never mutate that pack. Bare names are the legacy vanilla path and
        // deliberately retain the old editable generated PNG cache.
        if (path is not null && !File.Exists(path) && (cacheNamespacedFallbacks || !name.Contains(':')))
        {
            TrySavePng(path, pixels);
        }

        return pixels;
    }

    private static string ResolveLegacyTexturePath(string name) =>
        Path.Combine(TextureFolder, name + ".png");

    /// <summary>
    /// Rozlije barvu krycích texelů do průhledných sousedů. <b>Alfa se nemění.</b>
    ///
    /// <para><b>Proč to nestačí řešit při zmenšování.</b> Mipmapy váží barvu alfou, takže se
    /// do nich průhledné texely nezapočítají. Jenže samotné filtrování na GPU žádnou takovou
    /// úvahu nedělá — bilineární vzorek míchá RGB čtyř sousedů <b>bez ohledu na alfu</b>.
    /// U okraje stébla se tak do barvy přimíchá to, co leží v průhledné části, a je-li tam
    /// černá, vznikne tmavý lem. Zblízka není poznat, z dálky a při pohledu z boku ano —
    /// přesně jak to bylo hlášeno.</para>
    ///
    /// <para>Řešení je průhledné texely obarvit tím, co je vedle nich, aby filtr neměl co
    /// pokazit. Alfa zůstává nulová, takže se nic nezviditelní; mění se jen barva, kterou
    /// stejně nikdo nevidí — a právě proto v ní bývá černá.</para>
    ///
    /// <para>Rozlévá se dokola, ne přes celou dlaždici: pár kroků pokryje okolí každého tvaru
    /// a dál už se barva nikam nedostane, protože filtr sahá jen na sousední texel a mipmapy
    /// se počítají z už rozlitých dat.</para>
    /// </summary>
    private static void BleedColourIntoTransparent(byte[] rgba)
    {
        const int Rounds = 4;

        for (int round = 0; round < Rounds; round++)
        {
            bool changed = false;
            byte[] source = (byte[])rgba.Clone();

            for (int y = 0; y < TileSize; y++)
            {
                for (int x = 0; x < TileSize; x++)
                {
                    int index = ((y * TileSize) + x) * 4;

                    // Krycí texel má svou barvu; přepsat by ho byla škoda i chyba.
                    if (source[index + 3] > 0)
                    {
                        continue;
                    }

                    int r = 0, g = 0, b = 0, found = 0;

                    for (int dy = -1; dy <= 1; dy++)
                    {
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = x + dx;
                            int ny = y + dy;

                            if (nx < 0 || ny < 0 || nx >= TileSize || ny >= TileSize)
                            {
                                continue;
                            }

                            int neighbour = ((ny * TileSize) + nx) * 4;

                            // Sousedem je i texel obarvený v předchozím kole — proto se čte
                            // z kopie a ne z cíle, jinak by se barva rozlila celá naráz.
                            if (source[neighbour + 3] == 0 && source[neighbour] == 0
                                && source[neighbour + 1] == 0 && source[neighbour + 2] == 0)
                            {
                                continue;
                            }

                            r += source[neighbour];
                            g += source[neighbour + 1];
                            b += source[neighbour + 2];
                            found++;
                        }
                    }

                    if (found == 0)
                    {
                        continue;
                    }

                    rgba[index] = (byte)(r / found);
                    rgba[index + 1] = (byte)(g / found);
                    rgba[index + 2] = (byte)(b / found);
                    changed = true;
                }
            }

            if (!changed)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Vykreslí dlaždici generátorem v <see cref="ArtSize"/> a zvětší ji na <see cref="TileSize"/>.
    ///
    /// <para>Zvětšuje se <b>nejbližším sousedem</b>, tedy prostým kopírováním texelu do
    /// čtverce. Jakékoli filtrování by hrany rozmazalo, a právě ostré hrany jsou na těch
    /// kresbách to podstatné — u rostlin by se navíc rozmazal alfa kanál a kolem stébel
    /// by vznikly poloprůhledné lemy.</para>
    /// </summary>
    private static void DrawGenerated(string name, Span<byte> rgba)
    {
        Span<byte> art = stackalloc byte[ArtSize * ArtSize * 4];
        GenerateTile(name, art);

        int scale = TileSize / ArtSize;

        for (int y = 0; y < TileSize; y++)
        {
            int sourceRow = (y / scale) * ArtSize;

            for (int x = 0; x < TileSize; x++)
            {
                int source = (sourceRow + (x / scale)) * 4;
                int target = ((y * TileSize) + x) * 4;

                art.Slice(source, 4).CopyTo(rgba.Slice(target, 4));
            }
        }
    }

    /// <summary>Načte PNG a převede na RGBA. Vrací false, když se to z jakéhokoli důvodu nepovede.</summary>
    private static bool TryLoadPng(string path, Span<byte> rgba, bool isSkin, out Vector2 skinScale)
    {
        skinScale = Vector2.One;
        try
        {
            using FileStream stream = File.OpenRead(path);
            ImageResult image = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);

            // SKIN MODELU SMÍ BÝT NEČTVERCOVÝ A NESMÍ SE ROZMAZAT.
            //
            // Dlaždice bloku se opakuje, takže u ní na poměru stran záleží — roztažený
            // obrázek by ve zdi nesedl sám na sebe. Skin zvířete se ale neopakuje: je to
            // UV mapa, kde model odkazuje na místa v obrázku poměrem 0–1.
            //
            // Bez tohohle se 45 ze 105 skinů z Mobs Redo (64×32, 92×44, 31×39, 162×96…)
            // tiše zahodilo a zvíře dostalo náhradní kresbu z generátoru.
            //
            // Roztáhnout skin přes celou dlaždici by sice UV neposunulo, ale u pixel artu
            // je neceločíselné zvětšení poškození: pandě 92×44 vycházelo 1,39× vodorovně
            // a 2,91× svisle, takže některé sloupce byly dvakrát a jiné jednou. Proto se
            // skin zvětší jen celým násobkem, položí do levého horního rohu a zbytek
            // dlaždice se nechá prázdný. Kolik z dlaždice zabírá, si vezme model a přepočte
            // si tím UV — viz <see cref="SkinScale"/>.
            if (isSkin && image.Width > 0 && image.Height > 0)
            {
                int multiple = Math.Min(TileSize / image.Width, TileSize / image.Height);
                if (multiple >= 1)
                {
                    int usedWidth = image.Width * multiple;
                    int usedHeight = image.Height * multiple;
                    skinScale = new Vector2((float)usedWidth / TileSize, (float)usedHeight / TileSize);
                    for (int y = 0; y < usedHeight; y++)
                    {
                        int sourceRow = (y / multiple) * image.Width;

                        for (int x = 0; x < usedWidth; x++)
                        {
                            image.Data.AsSpan((sourceRow + (x / multiple)) * 4, 4)
                                .CopyTo(rgba.Slice(((y * TileSize) + x) * 4, 4));
                        }
                    }

                    return true;
                }

                // Skin větší než dlaždice se do ní celý nevejde (pavouci mají 162×96).
                // Zmenšit ho je ztráta, ale pořád lepší než náhradní kresba.
                for (int y = 0; y < TileSize; y++)
                {
                    int sourceRow = (y * image.Height / TileSize) * image.Width;

                    for (int x = 0; x < TileSize; x++)
                    {
                        image.Data.AsSpan((sourceRow + (x * image.Width / TileSize)) * 4, 4)
                            .CopyTo(rgba.Slice(((y * TileSize) + x) * 4, 4));
                    }
                }

                return true;
            }

            // MENŠÍ ČTVEREC SE ZVĚTŠÍ, cokoli jiného se odmítne.
            //
            // Zmenšovat se nesmí: dlaždice 128×128 stlačená na 64 rozmaže právě ty ostré
            // hrany, kvůli kterým pixel art vzniká. Zvětšit menší čtverec nejbližším
            // sousedem je naopak přesné — 16×16 vyjde texel na texel jako kresba z kódu,
            // která se zvětšuje úplně stejně.
            //
            // Bez tohohle musel mít každý ručně nakreslený obrázek přesně 64×64, což je
            // pro ikonu předmětu zbytečně jemné a pro kreslíře past: soubor se tiše
            // přeskočil a místo něj se objevila výchozí kresba.
            // Modelove UV atlasy mohou byt vetsi nez jedna GPU dlazdice (napr. 96x96).
            // Ctverec se prevzorkuje nejblizsim sousedem, takze se nerozmaze pixel art.
            if (image.Width != image.Height || image.Width <= 0)
            {
                return false;
            }

            if (image.Width == TileSize)
            {
                image.Data.AsSpan(0, rgba.Length).CopyTo(rgba);
                return true;
            }

            for (int y = 0; y < TileSize; y++)
            {
                int sourceRow = (y * image.Width / TileSize) * image.Width;

                for (int x = 0; x < TileSize; x++)
                {
                    image.Data.AsSpan((sourceRow + (x * image.Width / TileSize)) * 4, 4)
                        .CopyTo(rgba.Slice(((y * TileSize) + x) * 4, 4));
                }
            }

            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Načte z vysokého obrázku jeden vodorovný pruh o výšce dlaždice.
    /// </summary>
    /// <remarks>
    /// <para>Šířka musí sedět přesně, výška smí být násobek — právě proto to existuje.
    /// Přeškálování se tu stejně jako u obyčejné dlaždice nedělá: u pixel artu je to
    /// poškození, ne oprava.</para>
    ///
    /// <para>Pruh za koncem obrázku vyjde prázdný místo chyby. Model se tím nakreslí bez
    /// textury, což je pořád lepší než nespustit hru.</para>
    /// </remarks>
    private static bool TryLoadBand(string path, int band, Span<byte> rgba)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);
            ImageResult image = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);

            if (image.Width <= 0 || image.Height <= 0)
            {
                return false;
            }

            // PRUH JE ČTVEREC O STRANĚ ŠÍŘKY OBRÁZKU.
            //
            // Textury modelů jsou různě vysoké podle toho, kolik stěn se do nich skládá —
            // 64×256 u sekery, 32×32 u krumpáče. Čtvercový pruh z toho udělá jedno pravidlo
            // pro obojí: vysoká textura se rozřeže na několik, malá zůstane jedna.
            int side = image.Width;
            int top = band * side;

            if (band < 0 || top >= image.Height)
            {
                rgba.Clear();
                return true;
            }

            // ZVĚTŠUJE SE NEJBLIŽŠÍM SOUSEDEM, ne interpolací. U pixel artu je hladké
            // zvětšení poškození: rozmaže právě ty ostré hrany, kvůli kterým se kreslil.
            // Zmenšovat se nemusí — dlaždice je největší formát, který atlas nese.
            for (int y = 0; y < TileSize; y++)
            {
                int sourceY = top + (y * side / TileSize);

                for (int x = 0; x < TileSize; x++)
                {
                    int sourceX = x * side / TileSize;
                    int source = ((sourceY * image.Width) + sourceX) * 4;
                    int target = ((y * TileSize) + x) * 4;

                    if (source + 4 <= image.Data.Length)
                    {
                        image.Data.AsSpan(source, 4).CopyTo(rgba.Slice(target, 4));
                    }
                }
            }

            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Uloží vygenerovanou dlaždici, aby ji šlo přemalovat. Neúspěch se tiše přejde.</summary>
    private static void TrySavePng(string path, ReadOnlySpan<byte> rgba)
    {
        try
        {
            string? folder = Path.GetDirectoryName(path);

            if (!string.IsNullOrEmpty(folder))
            {
                Directory.CreateDirectory(folder);
            }

            using FileStream stream = File.Create(path);
            var writer = new StbImageWriteSharp.ImageWriter();

            writer.WritePng(
                rgba.ToArray(), TileSize, TileSize,
                StbImageWriteSharp.ColorComponents.RedGreenBlueAlpha, stream);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Zápis je pohodlí, ne podmínka. Na read-only instalaci se prostě negeneruje.
        }
    }

    /// <summary>
    /// Vyrobí obsah jedné dlaždice. Neznámý název dostane růžovo-černou šachovnici, aby
    /// chybějící textura byla ve scéně okamžitě vidět a nesplynula s okolím.
    /// </summary>
    /// <remarks>
    /// Veřejné schválně: bez možnosti vykreslit dlaždici mimo hru se textury ladí naslepo.
    /// U rostlin to stálo tři kola oprav, než se ukázalo, že se stébla slévají do plochy.
    /// </remarks>
    public static void GenerateTile(string name, Span<byte> rgba)
    {
        uint seed = Fnv1a(name);

        for (int y = 0; y < ArtSize; y++)
        {
            for (int x = 0; x < ArtSize; x++)
            {
                (byte r, byte g, byte b, byte a) = name switch
                {
                    "stone" => Speckled(x, y, seed, 122, 122, 128, 22),
                    "dirt" => Speckled(x, y, seed, 112, 82, 56, 20),
                    "sand" => Speckled(x, y, seed, 214, 202, 152, 14),
                    "grass_top" => Speckled(x, y, seed, 86, 142, 62, 22),
                    "grass_side" => GrassSide(x, y, seed, 86, 142, 62),
                    "dry_grass_top" => Speckled(x, y, seed, 154, 152, 78, 20),
                    "dry_grass_side" => GrassSide(x, y, seed, 154, 152, 78),
                    "snow" => Speckled(x, y, seed, 238, 242, 248, 8),
                    "sandstone" => Sandstone(x, y, seed, 206, 190, 140),
                    "red_sand" => Speckled(x, y, seed, 190, 106, 60, 16),
                    "gravel" => Gravel(x, y, seed),
                    "deepslate" => Speckled(x, y, seed, 62, 62, 68, 14),
                    "ice" => Speckled(x, y, seed, 166, 206, 232, 10),
                    "terracotta" => Sandstone(x, y, seed, 158, 96, 66),
                    "planks" => Planks(x, y, seed),
                    "glass" => Glass(x, y),
                    "water" => Water(x, y, seed),

                    // RUDY: kámen se zrny. Zrna se kreslí přes týž podklad jako obyčejný
                    // kámen, takže ruda ve stěně vypadá jako součást skály, ne jako
                    // nalepený obrázek.
                    // PRASKLINY PŘI TĚŽBĚ. Deset stupňů, jinak by postup nebyl vidět
                    // plynule. Kreslí se přes těžený blok, takže jsou z větší části průhledné.
                    "crack_0" => Crack(x, y, 0),
                    "crack_1" => Crack(x, y, 1),
                    "crack_2" => Crack(x, y, 2),
                    "crack_3" => Crack(x, y, 3),
                    "crack_4" => Crack(x, y, 4),
                    "crack_5" => Crack(x, y, 5),
                    "crack_6" => Crack(x, y, 6),
                    "crack_7" => Crack(x, y, 7),
                    "crack_8" => Crack(x, y, 8),
                    "crack_9" => Crack(x, y, 9),

                    "cobblestone" => Cobblestone(x, y, seed),
                    "coal_ore" => Ore(x, y, seed, 34, 34, 38),
                    "iron_ore" => Ore(x, y, seed, 196, 150, 116),
                    "copper_ore" => Ore(x, y, seed, 206, 112, 72),
                    "gold_ore" => Ore(x, y, seed, 242, 190, 38),
                    "lapis_ore" => Ore(x, y, seed, 42, 76, 196),
                    "redstone_ore" => Ore(x, y, seed, 206, 34, 38),
                    "diamond_ore" => Ore(x, y, seed, 96, 220, 226),
                    "emerald_ore" => Ore(x, y, seed, 48, 204, 104),
                    "ruby:ruby_ore" => Ore(x, y, seed, 224, 38, 76),

                    "furnace_side" => Cobblestone(x, y, seed),
                    "furnace_top" => Cobblestone(x, y, seed + 7u),
                    "furnace_front" => FurnaceFront(x, y, seed),

                    // ELEKTRINA. Plne barevne vrstvy se pouzivaji na proceduralni geometrii
                    // kabelu; ikony civek maji vlastni siluetu, aby v inventari nebyl ctverec.
                    "power_connector" => PowerTerminal(x, y, seed, relay: false, transformer: false),
                    "power_relay" => PowerTerminal(x, y, seed, relay: true, transformer: false),
                    "power_transformer" => PowerTerminal(x, y, seed, relay: false, transformer: true),
                    "cable_red" => Speckled(x, y, seed, 154, 34, 38, 8),
                    "cable_blue" => Speckled(x, y, seed, 34, 82, 174, 8),
                    "cable_yellow" => Speckled(x, y, seed, 210, 168, 36, 8),
                    "cable_green" => Speckled(x, y, seed, 36, 142, 64, 8),
                    "overhead_wire" => Speckled(x, y, seed, 42, 38, 34, 6),
                    "cable_junction" => JunctionBox(x, y, seed),

                    // IKONY PŘEDMĚTŮ. Kreslí se do téhož atlasu jako bloky, protože
                    // inventář vzorkuje tutéž texturu — druhý atlas by znamenal druhou
                    // sadu vazeb pro pár obrázků.
                    "stick" => Stick(x, y),
                    "apple" => Apple(x, y),
                    "oak_sapling" => Sapling(x, y, 62, 118, 48),
                    "spruce_sapling" => Sapling(x, y, 44, 88, 56),
                    "birch_sapling" => Sapling(x, y, 132, 158, 74),
                    "acacia_sapling" => Sapling(x, y, 108, 138, 54),
                    "maple_sapling" => Sapling(x, y, 150, 58, 42),
                    "coal" => Nugget(x, y, 38, 38, 42),
                    "charcoal" => Charcoal(x, y),
                    "player_arm_v2" => PlayerArm(x, y),

                    // IKONY ROZHRANÍ. Nejsou to předměty, ale kreslí se z téhož atlasu —
                    // druhá sada vazeb kvůli pěti obrázkům by se nevyplatila.
                    "ui_book" => Book(x, y),
                    "ui_slot_head" => ArmourHint(x, y, ArmourPart.Head),
                    "ui_slot_chest" => ArmourHint(x, y, ArmourPart.Chest),
                    "ui_slot_legs" => ArmourHint(x, y, ArmourPart.Legs),
                    "ui_slot_feet" => ArmourHint(x, y, ArmourPart.Feet),
                    "raw_iron" => Nugget(x, y, 188, 152, 122),
                    "iron_ingot" => Ingot(x, y, 216, 216, 222),
                    "raw_copper" => Nugget(x, y, 204, 112, 72),
                    "copper_ingot" => Ingot(x, y, 218, 126, 78),
                    "raw_gold" => Nugget(x, y, 226, 174, 40),
                    "gold_ingot" => Ingot(x, y, 246, 202, 54),
                    "raw_mutton" => RawMeat(x, y, 196, 82, 86),
                    "raw_venison" => RawMeat(x, y, 156, 54, 58),
                    "wolf_pelt" => Pelt(x, y, 72, 70, 68),
                    "lapis_lazuli" => Gem(x, y, 42, 76, 196),
                    "redstone_dust" => Nugget(x, y, 206, 34, 38),
                    "diamond" => Gem(x, y, 96, 220, 226),
                    "emerald" => Gem(x, y, 48, 204, 104),
                    "ruby:ruby" => Gem(x, y, 224, 38, 76),
                    "bucket" => Bucket(x, y),
                    "overhead_wire_coil" => CableCoil(x, y, 66, 58, 48),
                    "surface_cable_red" => CableCoil(x, y, 178, 42, 46),
                    "surface_cable_blue" => CableCoil(x, y, 42, 92, 196),
                    "surface_cable_yellow" => CableCoil(x, y, 226, 184, 44),
                    "surface_cable_green" => CableCoil(x, y, 42, 164, 72),
                    "wire_cutters" => WireCutters(x, y),

                    "flint_pickaxe" => Tool(x, y, ToolShape.Pickaxe, 74, 84, 112),
                    "stone_pickaxe" => Tool(x, y, ToolShape.Pickaxe, 130, 130, 136),
                    "iron_pickaxe" => Tool(x, y, ToolShape.Pickaxe, 216, 216, 222),
                    "diamond_pickaxe" => Tool(x, y, ToolShape.Pickaxe, 96, 220, 226),
                    "ruby:ruby_pickaxe" => Tool(x, y, ToolShape.Pickaxe, 224, 38, 76),
                    "flint_sword" => Tool(x, y, ToolShape.Sword, 74, 84, 112),
                    "stone_sword" => Tool(x, y, ToolShape.Sword, 130, 130, 136),
                    "iron_sword" => Tool(x, y, ToolShape.Sword, 216, 216, 222),
                    "diamond_sword" => Tool(x, y, ToolShape.Sword, 96, 220, 226),

                    "flint_axe" => Tool(x, y, ToolShape.Axe, 74, 84, 112),
                    "stone_axe" => Tool(x, y, ToolShape.Axe, 130, 130, 136),
                    "iron_axe" => Tool(x, y, ToolShape.Axe, 216, 216, 222),
                    "diamond_axe" => Tool(x, y, ToolShape.Axe, 96, 220, 226),
                    "flint_shovel" => Tool(x, y, ToolShape.Shovel, 74, 84, 112),
                    "stone_shovel" => Tool(x, y, ToolShape.Shovel, 130, 130, 136),
                    "iron_shovel" => Tool(x, y, ToolShape.Shovel, 216, 216, 222),
                    "diamond_shovel" => Tool(x, y, ToolShape.Shovel, 96, 220, 226),

                    // Vegetace. Kmen má bok se svislými vlákny a čelo s letokruhy,
                    // listí je poloprůhledná změť.
                    "oak_log_side" => LogSide(x, y, seed, 108, 82, 50),
                    "oak_log_top" => LogTop(x, y, seed, 148, 118, 76),
                    "oak_leaves" => Leaves(x, y, seed, 62, 118, 48),
                    "spruce_log_side" => LogSide(x, y, seed, 74, 54, 36),
                    "spruce_log_top" => LogTop(x, y, seed, 116, 92, 62),
                    "spruce_leaves" => Leaves(x, y, seed, 44, 88, 56),
                    "acacia_log_side" => LogSide(x, y, seed, 118, 70, 44),
                    "acacia_log_top" => LogTop(x, y, seed, 156, 108, 70),
                    "acacia_leaves" => Leaves(x, y, seed, 108, 138, 54),
                    "cactus_side" => CactusSide(x, y, seed),
                    "cactus_top" => Speckled(x, y, seed, 62, 110, 58, 12),

                    // Rostliny. Kreslí se na zkřížené plochy, takže je textura z větší
                    // části průhledná a záleží na tvaru, ne na výplni.
                    // Barvy jsou schválně TMAVŠÍ než travnatý blok (86, 142, 62). První
                    // verze měla trávu na 92, 148, 66, tedy prakticky totéž co zem —
                    // trs se v ní ztratil a vypadal jako nedodělaná textura.
                    "short_grass" => Blades(x, y, seed, 60, 112, 44, height: 10, blades: 7),
                    "tall_grass" => Blades(x, y, seed, 54, 104, 40, height: 15, blades: 10),
                    "dry_grass_tuft" => Blades(x, y, seed, 140, 126, 62, height: 9, blades: 6),
                    "fern" => Blades(x, y, seed, 42, 92, 50, height: 14, blades: 10, fern: true),
                    "dead_bush" => Blades(x, y, seed, 98, 72, 38, height: 12, blades: 6),
                    "flower_red" => Flower(x, y, seed, 196, 62, 58),
                    "flower_yellow" => Flower(x, y, seed, 224, 200, 72),
                    "flower_white" => Flower(x, y, seed, 236, 236, 228),

                    // Podvodní porost. Barvy jdou do MODROZELENA, ne do zelena jako tráva
                    // na souši: pod hladinou zbývá z dopadajícího světla nejmíň červené,
                    // takže rostlina, která by měla týž odstín jako louka, tam působí jako
                    // vystřižená z jiného obrázku.
                    //
                    // Chaluha je zároveň tmavší a řidší — má být vysoká a prosvítající,
                    // ne hustý trs.
                    // Ručně kreslené, ne generované — viz komentář u SeagrassArt.
                    "seagrass" => Painted(x, y, SeagrassArt, 44, 126, 98),
                    "kelp" => Painted(x, y, KelpArt, 34, 96, 74),
                    "red_algae" => Painted(x, y, RedAlgaeArt, 138, 62, 74),
                    "pale_weed" => Painted(x, y, PaleWeedArt, 96, 148, 140),
                    "sea_fern" => Painted(x, y, SeaFernArt, 28, 108, 92),

                    _ => Missing(x, y),
                };

                int offset = ((y * ArtSize) + x) * 4;
                rgba[offset + 0] = r;
                rgba[offset + 1] = g;
                rgba[offset + 2] = b;
                rgba[offset + 3] = a;
            }
        }
    }

    private static (byte R, byte G, byte B, byte A) Speckled(int x, int y, uint seed, int r, int g, int b, int amplitude)
    {
        int noise = (int)(Hash(x, y, seed) % (uint)(2 * amplitude)) - amplitude;
        return (Clamp(r + noise), Clamp(g + noise), Clamp(b + noise), 255);
    }

    /// <summary>Tvary nástrojů. Liší se jen hlavou, násada je společná.</summary>
    private enum ToolShape
    {
        Pickaxe,
        Axe,
        Shovel,
        Sword,
    }

    /// <summary>
    /// Ve kterém stupni se který texel rozpraskne. 255 znamená nikdy.
    /// </summary>
    /// <remarks>
    /// <para><b>Počítá se jednou dopředu, ne per texel.</b> Trhlina je čára, která někde
    /// začíná a někam vede — to se z jednoho pixelu rozhodnout nedá. Předpočítaná mapa
    /// zároveň zaručí, že vyšší stupeň <b>obsahuje</b> nižší: pixel jednou popraskaný už
    /// se nezacelí, takže praskliny při postupu rostou místo aby poskakovaly.</para>
    /// </remarks>
    private static readonly byte[] CrackStages = BuildCrackStages();

    private static byte[] BuildCrackStages()
    {
        var stages = new byte[ArtSize * ArtSize];
        Array.Fill(stages, (byte)255);

        // VŠECHNY TRHLINY VEDOU ZE STŘEDU. Blok praská od místa úderu ven, takže střed je
        // to jediné místo, odkud můžou vycházet — rozházet je po ploše byla chyba a vypadalo
        // to, jako by byl blok popsaný.
        //
        // Nepravidelnost nedělá poloha zárodku, ale SMĚRY A DÉLKY: sedm paprsků místo osmi
        // (osm se dělí čtyřmi a vyjde z toho vločka), každý jinak dlouhý a cestou se lámající.
        //
        // Číslo u paprsku je stupeň, ve kterém ZAČNE — jeho konec dorazí až u devítky, viz
        // Walk. První dva vznikají hned, takže na začátku je uprostřed jen drobná trhlinka.
        ReadOnlySpan<(int X, int Y, int Dx, int Dy, int Length, byte Stage)> seeds =
        [
            (7, 7, 1, -1, 8, 0),
            (7, 8, -1, 1, 6, 1),
            (8, 7, 1, 1, 9, 2),
            (7, 7, -1, -1, 5, 3),
            (7, 7, 0, -1, 7, 5),
            (8, 8, 1, 0, 6, 6),
            (7, 8, -1, 0, 8, 7),
        ];

        foreach ((int x, int y, int dx, int dy, int length, byte stage) in seeds)
        {
            Walk(stages, x, y, dx, dy, length, stage);
        }

        return stages;
    }

    /// <summary>
    /// Projde trhlinu od zárodku a označí texely, kudy vede.
    /// </summary>
    /// <remarks>
    /// <para><b>Stupeň se přiřazuje každému KROKU, ne celé trhlině.</b> Dokud ho měla celá
    /// trhlina, objevila se najednou jako hotová čára přes půl dlaždice — blok praskl
    /// a hned měl škrábanec. Trhlina musí ze středu <b>růst</b>: čím dál od zárodku, tím
    /// pozdější stupeň, takže se s postupem těžby prodlužuje.</para>
    ///
    /// <para>Směr se každý druhý krok zláme o jedno pole stranou. Bez toho je z trhliny
    /// rovná čára, což v kameni nevypadá jako prasklina, ale jako škrábnutí pravítkem.</para>
    /// </remarks>
    private static void Walk(byte[] stages, int x, int y, int dx, int dy, int length, byte start)
    {
        for (int step = 0; step < length; step++)
        {
            // Od stupně, ve kterém trhlina vznikne, po devítku na jejím konci. Trhliny,
            // které začínají později, tím rostou rychleji — což je správně: blok už je
            // rozlámaný a další praskliny se šíří snáz.
            byte stage = (byte)Math.Clamp(
                start + (int)MathF.Round(step / (float)length * (9 - start)), 0, 9);

            Mark(stages, x, y, stage);

            // Trhlina se rozšiřuje směrem od zárodku, takže u kraje je hrubší.
            if (step > length / 2)
            {
                Mark(stages, x + 1, y, (byte)Math.Min(9, stage + 1));
            }

            x += dx;
            y += dy;

            // Zlom stranou. Deterministický, aby se textura vygenerovala pokaždé stejně.
            if (step % 2 != 1)
            {
                continue;
            }

            uint turn = Hash(x, y, 91u) % 3u;

            if (turn == 0u)
            {
                x += dy;
                y -= dx;
            }
            else if (turn == 1u)
            {
                x -= dy;
                y += dx;
            }
        }
    }

    private static void Mark(byte[] stages, int x, int y, byte stage)
    {
        if (x < 0 || y < 0 || x >= ArtSize || y >= ArtSize)
        {
            return;
        }

        int index = (y * ArtSize) + x;

        if (stage < stages[index])
        {
            stages[index] = stage;
        }
    }

    /// <summary>
    /// Praskliny na těženém bloku. Stupeň 0 až 9.
    /// </summary>
    /// <remarks>
    /// <para>Kreslí se <b>tmavě šedou s poloprůhledností</b>, ne černou: úplně černá čára
    /// vypadá jako nakreslená fixou, kdežto prasklina je stín v prohlubni a nějakou barvu
    /// v sobě má. Ztmavení funguje na kameni i na písku, takže barva sedne na každý blok.</para>
    ///
    /// <para><b>Každý texel je jinak tmavý a jinak průhledný.</b> Jednolitá barva je to,
    /// co dělá z trhliny plochý obrazec — skutečná prasklina má hlubší i mělčí místa.
    /// Bere se z hashe polohy, takže je to stálé a pokaždé stejné.</para>
    /// </remarks>
    private static (byte R, byte G, byte B, byte A) Crack(int x, int y, int stage)
    {
        byte appears = CrackStages[(y * ArtSize) + x];

        if (appears > stage)
        {
            return (0, 0, 0, 0);
        }

        uint noise = Hash(x, y, 313u);

        // Jádro trhliny (to, co prasklo dřív) je hlubší, a tedy tmavší a méně průhledné.
        int depth = Math.Max(0, 4 - (stage - appears));

        // Tmavá, ale ne uhlově černá. Struktura zůstává v rozptylu, ne ve světlosti:
        // rozsah tónu je úzký a nízko, takže je prasklina temná a přitom není jednolitá.
        int tone = 12 + (int)(noise % 22u) + (depth * 2);
        int alpha = 176 + (int)((noise >> 8) % 40u) + (depth * 6);

        return (Clamp(tone), Clamp(tone + 2), Clamp(tone + 5), (byte)Math.Clamp(alpha, 0, 235));
    }

    /// <summary>Dlažební kámen: hrubší zrno než skála a tmavé spáry.</summary>
    private static (byte R, byte G, byte B, byte A) Cobblestone(int x, int y, uint seed)
    {
        // Nepravidelné kameny: mřížka 4×4 posunutá po řádcích, aby spáry nebyly v jedné linii.
        int row = y / 4;
        int shifted = (x + (row * 2)) % 16;

        bool seam = shifted % 4 == 0 || y % 4 == 0;

        int shade = (int)(Hash(x / 2, y / 2, seed) % 30) - 15;

        return seam
            ? Speckled(x, y, seed, 78, 78, 84, 10)
            : (Clamp(126 + shade), Clamp(126 + shade), Clamp(132 + shade), (byte)255);
    }

    /// <summary>
    /// Ruda: obyčejný kámen se zrny.
    /// </summary>
    /// <remarks>
    /// Zrna se kreslí přes týž podklad jako blok kamene a se stejným šumem, takže ruda
    /// ve stěně navazuje na skálu kolem. Kdyby měla vlastní pozadí, byla by v okolní
    /// skále vidět jako nalepený čtverec.
    /// </remarks>
    private static (byte R, byte G, byte B, byte A) Ore(int x, int y, uint seed, int r, int g, int b)
    {
        // Zrna se shlukují: bere se hrubší mřížka, takže vznikne pár chuchvalců místo
        // rovnoměrného kropení.
        uint blob = Hash(x / 3, y / 3, seed + 31u);
        uint speck = Hash(x, y, seed + 57u);

        bool grain = blob % 5u < 2u && speck % 3u < 2u;

        if (!grain)
        {
            return Speckled(x, y, seed, 122, 122, 128, 22);
        }

        int shade = (int)(speck % 40u) - 20;

        return (Clamp(r + shade), Clamp(g + shade), Clamp(b + shade), (byte)255);
    }

    /// <summary>Čelo pece: dlažba s tmavým otvorem a roštem.</summary>
    private static (byte R, byte G, byte B, byte A) FurnaceFront(int x, int y, uint seed)
    {
        bool mouth = x is >= 4 and <= 11 && y is >= 6 and <= 12;

        if (!mouth)
        {
            return Cobblestone(x, y, seed);
        }

        // Rošt: dvě svislé mřížky přes otvor, aby ústí nebylo jen černý obdélník.
        bool bar = x is 6 or 9;

        return bar ? ((byte)70, (byte)62, (byte)56, (byte)255) : ((byte)26, (byte)22, (byte)20, (byte)255);
    }

    /// <summary>Klacek: šikmá násada přes úhlopříčku.</summary>
    private static (byte R, byte G, byte B, byte A) Stick(int x, int y)
    {
        int distance = Math.Abs((15 - y) - x);

        return distance <= 1 && x is >= 3 and <= 12
            ? ((byte)134, (byte)98, (byte)56, (byte)255)
            : ((byte)0, (byte)0, (byte)0, (byte)0);
    }

    /// <summary>Jablko: kulaté, se stopkou a odleskem.</summary>
    private static (byte R, byte G, byte B, byte A) Apple(int x, int y)
    {
        if (x is 7 or 8 && y is >= 2 and <= 4)
        {
            return (86, 62, 38, 255);
        }

        float dx = x - 7.5f;
        float dy = y - 9.5f;

        if ((dx * dx * 1.15f) + (dy * dy) > 24f)
        {
            return (0, 0, 0, 0);
        }

        // Odlesk vlevo nahoře, aby koule nebyla plochý kruh.
        bool shine = dx is > -3.5f and < -1.5f && dy is > -3f and < -1f;

        return shine ? ((byte)236, (byte)150, (byte)140, (byte)255) : ((byte)176, (byte)38, (byte)34, (byte)255);
    }

    /// <summary>Sazenice: stonek s pár lístky. Barva se bere z druhu stromu.</summary>
    private static (byte R, byte G, byte B, byte A) Sapling(int x, int y, int r, int g, int b)
    {
        if (x is 7 or 8 && y is >= 8 and <= 14)
        {
            return (94, 74, 46, 255);
        }

        float dx = x - 7.5f;
        float dy = y - 7f;

        if ((dx * dx) + (dy * dy * 1.6f) > 20f)
        {
            return (0, 0, 0, 0);
        }

        // Řídká koruna: každý třetí texel vynechaný, aby to byl keřík a ne kolečko.
        if (((x * 3) + (y * 5)) % 4 == 0)
        {
            return (0, 0, 0, 0);
        }

        int shade = ((x + y) % 3) * 10;

        return (Clamp(r - shade), Clamp(g - shade), Clamp(b - shade), (byte)255);
    }

    /// <summary>Hrouda suroviny: nepravidelný chuchvalec uprostřed.</summary>
    private static (byte R, byte G, byte B, byte A) Nugget(int x, int y, int r, int g, int b)
    {
        float dx = x - 7.5f;
        float dy = y - 8.5f;

        float radius = 4.2f + (MathF.Sin((x + y) * 1.7f) * 0.8f);

        if ((dx * dx) + (dy * dy) > radius * radius)
        {
            return (0, 0, 0, 0);
        }

        // Světlo shora vlevo, aby hrouda nebyla plochý kruh.
        int shade = (int)(-(dx + dy) * 2.5f);

        return (Clamp(r + shade), Clamp(g + shade), Clamp(b + shade), (byte)255);
    }

    private static (byte R, byte G, byte B, byte A) RawMeat(int x, int y, int r, int g, int b)
    {
        float dx = x - 7.5f;
        float dy = y - 8.2f;
        float outline = (dx * dx * 0.72f) + (dy * dy);
        if (outline > 24f || (x <= 4 && y <= 5) || (x >= 12 && y >= 11))
            return (0, 0, 0, 0);

        bool fat = (x + (y * 2)) % 7 == 0 || (x is 5 or 10 && y is >= 6 and <= 10);
        if (fat)
            return (236, 188, 170, 255);

        int shade = ((x * 11 + y * 7) % 13) - 6;
        return (Clamp(r + shade), Clamp(g + shade), Clamp(b + shade), 255);
    }

    private static (byte R, byte G, byte B, byte A) Pelt(int x, int y, int r, int g, int b)
    {
        int inset = y is < 3 or > 12 ? 4 : y is < 5 or > 10 ? 2 : 1;
        if (x < inset || x > 15 - inset)
            return (0, 0, 0, 0);

        bool edge = x == inset || x == 15 - inset || y is 2 or 13;
        int grain = ((x * 5 + y * 9) % 17) - 8;
        int shade = edge ? -30 : grain;
        return (Clamp(r + shade), Clamp(g + shade), Clamp(b + shade), 255);
    }

    /// <summary>Které místo na těle obrys ve slotu výstroje označuje.</summary>
    private enum ArmourPart
    {
        Head,
        Chest,
        Legs,
        Feet,
    }

    /// <summary>
    /// Kniha receptů: zavřená knížka z boku, s hřbetem a listy.
    /// </summary>
    /// <remarks>
    /// Musí být poznat i v osmině slotu, takže je to hlavně kontrast: tmavý hřbet vlevo,
    /// světlé listy vpravo. Detaily by se v té velikosti stejně slily.
    /// </remarks>
    private static (byte R, byte G, byte B, byte A) Book(int x, int y)
    {
        if (x is < 2 or > 13 || y is < 3 or > 13)
        {
            return (0, 0, 0, 0);
        }

        // Hřbet vlevo, o odstín tmavší než desky.
        if (x <= 4)
        {
            return (118, 44, 40, 255);
        }

        // Listy: světlé pruhy s tmavou linkou mezi nimi, ať je poznat, že je to svazek.
        int shade = y % 3 == 0 ? -26 : 0;

        return (Clamp(226 + shade), Clamp(222 + shade), Clamp(202 + shade), (byte)255);
    }

    /// <summary>
    /// Bledý obrys do prázdného slotu výstroje.
    /// </summary>
    /// <remarks>
    /// Bez něj jsou to čtyři stejné čtverce a hráč musí zkoušet, kam co patří. Kreslí se
    /// plnou barvou a průhlednost si dodá rozhraní, aby týž obrázek šel použít i jinde.
    /// </remarks>
    private static (byte R, byte G, byte B, byte A) ArmourHint(int x, int y, ArmourPart part)
    {
        bool inside = part switch
        {
            // Přilba: kopule s výřezem na obličej.
            ArmourPart.Head => x is >= 4 and <= 11 && y is >= 3 and <= 12
                && !(y >= 9 && x is >= 6 and <= 9),

            // Kyrys: trup s rameny.
            ArmourPart.Chest => (y is >= 4 and <= 6 && x is >= 3 and <= 12)
                || (y is >= 7 and <= 12 && x is >= 5 and <= 10),

            // Nohavice: dvě nohy oddělené mezerou.
            ArmourPart.Legs => y is >= 3 and <= 12 && x is >= 4 and <= 11
                && !(y >= 6 && x is >= 7 and <= 8),

            // Boty: nízké, s vytaženou špičkou.
            _ => (y is >= 8 and <= 12 && x is >= 4 and <= 11)
                || (y is >= 10 and <= 12 && x is >= 2 and <= 3),
        };

        return inside ? ((byte)206, (byte)210, (byte)220, (byte)255) : ((byte)0, (byte)0, (byte)0, (byte)0);
    }

    /// <summary>
    /// Pixelová kůže hranaté paže v UV rozbalení 16 × 16.
    /// </summary>
    /// <remarks>
    /// Horní pás obsahuje dvě čela 4 × 4, dolní pás čtyři boky 4 × 12. Každý texel je
    /// neprůhledný: alfa nesmí z kvádru znovu vyříznout starou siluetu pěsti. Poslední tři
    /// řádky boků tvoří modrou manžetu, zbytek jsou čtyři mírně odlišně osvětlené strany kůže.
    /// </remarks>
    private static (byte R, byte G, byte B, byte A) PlayerArm(int x, int y)
    {
        // Nepoužitá políčka atlasu jsou také krycí. Mipmapa tak nemůže k okrajům čel
        // přimíchat průhlednost ani na větší vzdálenost.
        if (y < 4)
        {
            if (x is >= 4 and < 8)
            {
                int localX = x - 4;
                int localY = y;
                int edge = localX is 0 or 3 || localY is 0 or 3 ? -8 : 5;
                int detail = (localX == 1 && localY == 2) ? -7 : 0;
                return (Clamp(220 + edge + detail), Clamp(164 + edge + detail),
                    Clamp(123 + edge + detail), 255);
            }

            if (x is >= 8 and < 12)
            {
                int weave = ((x + y) & 1) == 0 ? 5 : -4;
                return (Clamp(76 + weave), Clamp(91 + weave), Clamp(126 + weave), 255);
            }

            return (72, 86, 119, 255);
        }

        int face = x / 4;
        int localXOnFace = x & 3;
        int localYOnFace = y - 4;

        // Tři řádky u hráčova těla jsou rukáv, první z nich tmavší lem manžety.
        if (localYOnFace >= 9)
        {
            int faceShade = face switch { 0 => -10, 1 => 2, 2 => 7, _ => -5 };
            int cuff = localYOnFace == 9 ? -18 : 0;
            int weave = ((localXOnFace + localYOnFace) & 1) == 0 ? 4 : -3;
            int shade = faceShade + cuff + weave;
            return (Clamp(78 + shade), Clamp(94 + shade), Clamp(132 + shade), 255);
        }

        int sideShade = face switch { 0 => -13, 1 => 2, 2 => 10, _ => -7 };
        int grain = ((localXOnFace * 5 + localYOnFace * 3 + face) % 7) - 3;
        int crease = 0;

        // Jemné klouby a palcová hrana bez černých čar přes celou paži.
        if (localYOnFace is 1 or 2 && localXOnFace == ((face + localYOnFace) & 3))
        {
            crease = -11;
        }
        else if (face == 1 && localYOnFace is >= 4 and <= 6 && localXOnFace == 0)
        {
            crease = -7;
        }

        int shadeSkin = sideShade + grain + crease;
        return (Clamp(216 + shadeSkin), Clamp(162 + shadeSkin),
            Clamp(123 + shadeSkin), 255);
    }

    private static (byte R, byte G, byte B, byte A) PowerTerminal(
        int x, int y, uint seed, bool relay, bool transformer)
    {
        if (x is 0 or 15 || y is 0 or 15)
        {
            return (46, 48, 54, 255);
        }

        if (transformer && (x is >= 3 and <= 5 || x is >= 10 and <= 12))
        {
            return (172, 102, 42, 255);
        }

        int dx = x - 8;
        int dy = y - 8;
        int radius = (dx * dx) + (dy * dy);
        if (radius <= (relay ? 18 : 10))
        {
            return relay
                ? ((byte)206, (byte)150, (byte)44, (byte)255)
                : ((byte)184, (byte)116, (byte)48, (byte)255);
        }

        return Speckled(x, y, seed, 108, 112, 120, 12);
    }

    private static (byte R, byte G, byte B, byte A) JunctionBox(int x, int y, uint seed)
    {
        if (x is 0 or 15 || y is 0 or 15)
        {
            return (42, 46, 52, 255);
        }

        bool screw = (x is 2 or 13) && (y is 2 or 13);
        if (screw)
        {
            return (188, 194, 202, 255);
        }

        return Speckled(x, y, seed, 82, 88, 96, 8);
    }

    private static (byte R, byte G, byte B, byte A) CableCoil(int x, int y, int r, int g, int b)
    {
        int dx = x - 8;
        int dy = y - 8;
        int radius = (dx * dx) + (dy * dy);

        if (radius is >= 20 and <= 48)
        {
            return ((byte)r, (byte)g, (byte)b, 255);
        }

        if (radius < 20 && radius > 7)
        {
            return (54, 48, 42, 255);
        }

        return (0, 0, 0, 0);
    }

    private static (byte R, byte G, byte B, byte A) WireCutters(int x, int y)
    {
        bool leftJaw = y <= 7 && x is >= 3 and <= 6 && x + y is >= 8 and <= 12;
        bool rightJaw = y <= 7 && x is >= 9 and <= 12 && (15 - x) + y is >= 8 and <= 12;
        bool hinge = (x - 8) * (x - 8) + (y - 8) * (y - 8) <= 4;
        bool leftHandle = y >= 8 && x is >= 4 and <= 7 && x + y <= 19;
        bool rightHandle = y >= 8 && x is >= 9 and <= 12 && (15 - x) + y <= 18;

        if (leftJaw || rightJaw) return (188, 192, 202, 255);
        if (hinge) return (64, 66, 72, 255);
        if (leftHandle || rightHandle) return (168, 44, 42, 255);
        return (0, 0, 0, 0);
    }

    /// <summary>
    /// Dřevěné uhlí: hranatý oharek s podélnými prasklinami.
    /// </summary>
    /// <remarks>
    /// <b>Nesmí být jen tmavší hrouda.</b> Uhlí je v inventáři hned vedle a dvě černé kuličky
    /// od sebe hráč nerozezná. Oharek je proto hranatý místo kulatého, má letokruhy po
    /// délce a do černé je přimíchaná hnědá — pořád je to uhlí, ale je poznat, že bylo dřevo.
    /// </remarks>
    private static (byte R, byte G, byte B, byte A) Charcoal(int x, int y)
    {
        // Hranol nakloněný na stranu: užší nahoře, širší dole.
        int top = 3;
        int bottom = 13;

        if (y < top || y > bottom)
        {
            return (0, 0, 0, 0);
        }

        float t = (y - top) / (float)(bottom - top);
        int left = (int)MathF.Round(5f - (t * 2.0f));
        int right = (int)MathF.Round(10f + (t * 2.0f));

        if (x < left || x > right)
        {
            return (0, 0, 0, 0);
        }

        // Praskliny podél vlákna: svislé pruhy, ne šum. Dřevo praská po délce.
        bool crack = ((x * 3) + (y / 5)) % 5 == 0;

        int shade = (int)(-(x - 8) * 1.6f) - (int)(t * 10f);

        if (crack)
        {
            shade -= 12;
        }

        // Hnědý nádech odlišuje oharek od černého uhlí.
        return (Clamp(52 + shade + 8), Clamp(40 + shade), Clamp(34 + shade), (byte)255);
    }

    /// <summary>Ingot: nízký lichoběžník se světlým horním okrajem.</summary>
    private static (byte R, byte G, byte B, byte A) Ingot(int x, int y, int r, int g, int b)
    {
        if (y is < 6 or > 11)
        {
            return (0, 0, 0, 0);
        }

        int inset = y < 8 ? 4 : 3;

        if (x < inset || x > 15 - inset)
        {
            return (0, 0, 0, 0);
        }

        int shade = y < 8 ? 24 : -(y - 8) * 8;

        return (Clamp(r + shade), Clamp(g + shade), Clamp(b + shade), (byte)255);
    }

    /// <summary>Drahokam: kosočtverec s odleskem.</summary>
    private static (byte R, byte G, byte B, byte A) Gem(int x, int y, int r, int g, int b)
    {
        int distance = Math.Abs(x - 7) + Math.Abs(y - 8);

        if (distance > 5)
        {
            return (0, 0, 0, 0);
        }

        int shade = distance <= 2 ? 40 : -distance * 6;

        return (Clamp(r + shade), Clamp(g + shade), Clamp(b + shade), (byte)255);
    }

    /// <summary>Kbelík: kovový komolý kužel s uchem.</summary>
    private static (byte R, byte G, byte B, byte A) Bucket(int x, int y)
    {
        bool handle = y is >= 3 and <= 5 && (x is 3 or 12 || (y == 3 && x is >= 4 and <= 11));

        if (handle)
        {
            return (150, 150, 158, 255);
        }

        if (y is < 6 or > 14)
        {
            return (0, 0, 0, 0);
        }

        int inset = (y - 6) / 4;

        if (x < 3 + inset || x > 12 - inset)
        {
            return (0, 0, 0, 0);
        }

        int shade = x < 6 ? 22 : -(x - 6) * 3;

        return (Clamp(178 + shade), Clamp(178 + shade), Clamp(186 + shade), (byte)255);
    }

    /// <summary>
    /// Nástroj: dřevěná násada po úhlopříčce a hlava v pravém horním rohu.
    /// </summary>
    /// <remarks>
    /// Tvar hlavy rozlišuje nástroje na první pohled, protože barva rozlišuje stupeň.
    /// Kdyby se lišila jen barva, nešel by v pásu poznat krumpáč od lopaty.
    /// </remarks>
    private static (byte R, byte G, byte B, byte A) Tool(int x, int y, ToolShape shape, int r, int g, int b)
    {
        // MEČ MÁ JINOU STAVBU NEŽ NÁŘADÍ: čepel jde po celé úhlopříčce, násada je jen
        // krátký jílec dole. Skládat ho ze stejných dílů jako krumpáč by dalo nářadí
        // s divným hrotem, ne zbraň.
        if (shape == ToolShape.Sword)
        {
            bool blade = Math.Abs((14 - y) - x) <= 1 && x is >= 4 and <= 13;
            bool guard = y is 10 or 11 && x is >= 1 and <= 6 && Math.Abs((14 - y) - x) <= 3;
            bool grip = Math.Abs((14 - y) - x) <= 1 && x is >= 1 and <= 3;

            if (blade)
            {
                int gleam = ((x + y) % 3) * 10;
                return (Clamp(r - gleam), Clamp(g - gleam), Clamp(b - gleam), (byte)255);
            }

            if (guard)
            {
                return (168, 132, 60, 255);
            }

            return grip ? ((byte)96, (byte)66, (byte)38, (byte)255) : ((byte)0, (byte)0, (byte)0, (byte)0);
        }

        // Násada: úzký pruh po úhlopříčce z levého dolního rohu.
        if (Math.Abs((14 - y) - x) <= 1 && x is >= 2 and <= 10 && y >= 5)
        {
            return (124, 88, 50, 255);
        }

        int hx = x - 10;
        int hy = y - 4;

        bool head = shape switch
        {
            // Krumpáč: dva hroty do stran, uprostřed užší.
            ToolShape.Pickaxe => hy is >= -2 and <= 1 && x is >= 5 and <= 14
                && Math.Abs(x - 10) + Math.Abs(hy) <= 6,

            // Sekera: klín přisazený k násadě zprava.
            ToolShape.Axe => hx is >= -3 and <= 2 && hy is >= -2 and <= 4
                && hx + 3 >= Math.Abs(hy - 1),

            // Lopata: úzký list dolů.
            _ => hx is >= -2 and <= 1 && hy is >= -2 and <= 3,
        };

        if (!head)
        {
            return (0, 0, 0, 0);
        }

        int shade = ((x + y) % 3) * 8;

        return (Clamp(r - shade), Clamp(g - shade), Clamp(b - shade), (byte)255);
    }

    private static (byte R, byte G, byte B, byte A) GrassSide(int x, int y, uint seed, int r, int g, int b)
    {
        // Nahoře pruh trávy s roztřepeným okrajem, pod ním hlína.
        int edge = 4 + (int)(Hash(x, 0, seed) % 3);
        return y < edge
            ? Speckled(x, y, seed, r, g, b, 20)
            : Speckled(x, y, seed, 112, 82, 56, 18);
    }

    /// <summary>Pískovec: vodorovné vrstvy usazeniny, aby nesplynul s pískem nad sebou.</summary>
    private static (byte R, byte G, byte B, byte A) Sandstone(int x, int y, uint seed, int r, int g, int b)
    {
        // Pásy po čtyřech texelech s mírně jiným odstínem; okraj pásu je tmavší spára.
        int band = ((y / 4) % 2) * 8;
        int seam = y % 4 == 0 ? -12 : 0;

        return Speckled(x, y, seed, r - band + seam, g - band + seam, b - band + seam, 7);
    }

    /// <summary>Bok kmene: svislá vlákna, tedy šum protáhlý ve svislém směru.</summary>
    private static (byte R, byte G, byte B, byte A) LogSide(int x, int y, uint seed, int r, int g, int b)
    {
        // Hash bez y dělá pruh přes celou výšku; slabá příměs s y ho rozbije, aby
        // nevypadal jako tapeta.
        int fiber = (int)(Hash(x, 0, seed) % 26) - 13;
        int grain = (int)(Hash(x, y / 3, seed + 7) % 10) - 5;

        return (Clamp(r + fiber + grain), Clamp(g + fiber + grain), Clamp(b + fiber + grain), 255);
    }

    /// <summary>Čelo kmene: letokruhy jako soustředné čtverce kolem středu.</summary>
    private static (byte R, byte G, byte B, byte A) LogTop(int x, int y, uint seed, int r, int g, int b)
    {
        int half = ArtSize / 2;
        int ring = Math.Max(Math.Abs(x - half), Math.Abs(y - half));
        int shade = (ring % 3 == 0 ? -18 : 0) + ((int)(Hash(x, y, seed) % 12) - 6);

        return (Clamp(r + shade), Clamp(g + shade), Clamp(b + shade), 255);
    }

    /// <summary>
    /// Listí: hrubá změť s dírami.
    ///
    /// <para>Díry jsou průhledné, ne jen tmavé — proto má listí v definici bloku
    /// <c>opaque: false</c>. Bez děr vypadá koruna jako zelená bedna.</para>
    /// </summary>
    private static (byte R, byte G, byte B, byte A) Leaves(int x, int y, uint seed, int r, int g, int b)
    {
        uint hash = Hash(x, y, seed);
        int shade = (int)((hash >> 8) % 46) - 23;

        // Zhruba každý sedmý texel je díra. Míň je málo, víc a koruna zprůsvitní.
        //
        // DÍRA MÁ V BARVĚ POŘÁD LISTÍ, jen nulovou průhlednost. Zní to zbytečně, ale není:
        // vzdálený terén kreslí les touhle texturou v NEPRŮHLEDNÉM průchodu, který alfu
        // ignoruje. S černou v dírách by z lesů na obzoru byly černé skvrny.
        byte alpha = hash % 7 == 0 ? (byte)0 : (byte)255;

        return (Clamp(r + shade), Clamp(g + shade), Clamp(b + shade), alpha);
    }

    /// <summary>
    /// Stébla: pár svislých čar, které se nahoru mírně uhýbají do stran.
    ///
    /// <para>Textura je z většiny průhledná — u rostliny rozhoduje tvar, ne výplň. Stébla
    /// se počítají z hashe názvu, takže tráva a kapradí mají jinou kresbu, ale obojí
    /// vypadá jako trs, ne jako šum.</para>
    /// </summary>
    /// <summary>
    /// Rozvržení trsu: kořen, výška a o kolik texelů se stéblo nahoře ukloní.
    ///
    /// <para><b>Napevno, ne z náhody.</b> Původně se kořeny i ohyby losovaly z hashe
    /// a dopadlo to špatně: stébla se místy potkala v témž sloupci a slila se do plné
    /// plochy, jinde zůstala osamocená jako drátek. Trs byl z jedné strany vyplněný
    /// a z druhé řídký. Na šestnácti texelech je rostlina tak malá, že se každý texel
    /// počítá a náhoda tvar jenom kazí.</para>
    ///
    /// <para>Kořeny jsou rozprostřené a úklony se vějířovitě rozbíhají ven, takže je trs
    /// symetrický a stébla se nepřekrývají.</para>
    /// </summary>
    /// <remarks>
    /// Kořeny jsou schválně rozprostřené s mezerami, ne namačkané k sobě. Kvadratický
    /// ohyb se u země ještě neprojeví, takže spodní řádky leží přesně na kořenech —
    /// se sousedními kořeny z toho byl plný pruh, ze kterého nahoře trčely jen špičky.
    /// </remarks>
    private static readonly (int Root, int Height, int Lean)[] TuftBlades =
    [
        (8, 14, 0),
        (5, 12, -2),
        (11, 12, 2),
        (3, 10, -3),
        (13, 10, 3),
        (6, 8, -1),
        (10, 8, 1),
        (1, 7, -1),
        (14, 7, 1),
        (7, 5, 0),
    ];

    /// <summary>Kapradí: páry lístků po obou stranách jednoho stvolu.</summary>
    private static readonly (int Root, int Height, int Lean)[] FernBlades =
    [
        (7, 15, 0),
        (6, 12, -3),
        (8, 12, 3),
        (6, 9, -4),
        (8, 9, 4),
        (6, 6, -4),
        (8, 6, 4),
        (7, 3, -2),
        (7, 3, 2),
        (7, 10, 0),
    ];

    /// <param name="height">Kolik texelů od spodní hrany nejvyšší stéblo sahá.</param>
    /// <param name="blades">Kolik stébel z rozvržení se použije.</param>
    /// <param name="fern">Použít symetrické rozvržení kapradí místo trsu trávy.</param>
    /// <summary>
    /// Podvodní stuha místo suchozemského stébla.
    ///
    /// <para><b>Proč to nejde nakreslit funkcí <see cref="Blades"/>.</b> Ta dělá trs: krátká
    /// stébla vyrůstající z jednoho místa a rozbíhající se do vějíře, protože tak roste tráva
    /// na louce. Vodní rostlina se chová opačně — je dlouhá, úzká, sahá skoro k hladině
    /// a vlní se v proudu po celé délce. Na snímku pod vodou byl rozdíl okamžitě vidět:
    /// vypadalo to jako kus louky přenesený na dno.</para>
    ///
    /// <para>Ohyb je proto <b>sinusový po celé výšce</b>, ne kvadratický od kořene, a každá
    /// stuha má vlastní fázi, aby se nevlnily svorně jako jedna plachta.</para>
    /// </summary>
    /// <summary>
    /// Vodní rostliny se kreslí <b>ručně, texel po texelu</b>, ne z funkce.
    ///
    /// <para><b>Proč.</b> Tři pokusy vygenerovat je vzorcem skončily špatně: stuhy z několika
    /// sinusovek se v překryvu dvou zkřížených ploch prořezávaly do změti, a když se z toho
    /// stal jeden stvol s listy podle vzorce, vyrostly z něj pravidelné vodorovné čárky —
    /// na snímku doslova řada antén. Vzorec neumí nakreslit tvar, umí jen vzor.</para>
    ///
    /// <para><b>PRVNÍ A POSLEDNÍ ŘÁDEK MUSÍ SEDĚT.</b> Rostlina se skládá z několika bloků
    /// nad sebou a každý kreslí tutéž dlaždici, takže spodní hrana jedné musí navazovat na
    /// horní hranu druhé. Odsud ty mezery v předchozích verzích. Kdo bude kresby měnit,
    /// musí to zkontrolovat — jinak se stvol rozpadne na kusy.</para>
    ///
    /// <para>Znak <c>#</c> je jádro (světlejší), <c>+</c> okraj či list (tmavší), tečka je
    /// průhledno.</para>
    /// </summary>
    private static readonly string[] SeagrassArt =
    [
        "......+#.+#.....",
        "......+#.+#.....",
        ".....+#..+#.....",
        ".....+#...#+....",
        "....+#....#+....",
        "....+#....+#....",
        "....#+.....#....",
        "...+#......#+...",
        "...+#.....+#....",
        "....#+....+#....",
        "....+#....#+....",
        ".....#+..+#.....",
        ".....+#..+#.....",
        "......#+.+#.....",
        "......+#.+#.....",
        "......+#.+#.....",
    ];

    private static readonly string[] KelpArt =
    [
        "......+##+......",
        "..+...+##+...+..",
        ".++...+##+...++.",
        "..++..+##+..++..",
        "...+..+##+..+...",
        "......+##+......",
        "......+##+......",
        "...+..+##+..+...",
        "..++..+##+..++..",
        ".++...+##+...++.",
        "..+...+##+...+..",
        "......+##+......",
        "......+##+......",
        "......+##+......",
        "......+##+......",
        "......+##+......",
    ];

    private static readonly string[] RedAlgaeArt =
    [
        ".....+#..#+.....",
        "....+#....#+....",
        "...+#..++..#+...",
        "...#..+##+..#...",
        "..+#..+##+..#+..",
        "..+#...++...#+..",
        "...#........#...",
        "...+#......#+...",
        "....#+....+#....",
        "....+#....#+....",
        ".....#+..+#.....",
        ".....+#..#+.....",
        "......#..#......",
        "......#..#......",
        ".....+#..#+.....",
        ".....+#..#+.....",
    ];

    private static readonly string[] PaleWeedArt =
    [
        ".......##.......",
        ".......##.......",
        "......+##.......",
        "......+##+......",
        ".......##+......",
        ".......##.......",
        "......+##.......",
        "......+##+......",
        ".......##+......",
        ".......##.......",
        "......+##.......",
        "......+##+......",
        ".......##+......",
        ".......##.......",
        ".......##.......",
        ".......##.......",
    ];

    private static readonly string[] SeaFernArt =
    [
        "......+#+.......",
        "..++..+#+..++...",
        "...++.+#+.++....",
        "....+.+#+.+.....",
        "......+#+.......",
        "..++..+#+..++...",
        "...++.+#+.++....",
        "....+.+#+.+.....",
        "......+#+.......",
        "..++..+#+..++...",
        "...++.+#+.++....",
        "....+.+#+.+.....",
        "......+#+.......",
        "......+#+.......",
        "......+#+.......",
        "......+#+.......",
    ];

    /// <summary>Vybarví ručně nakreslenou rostlinu.</summary>
    private static (byte R, byte G, byte B, byte A) Painted(
        int x, int y, string[] art, int r, int g, int b)
    {
        char cell = art[y][x];

        return cell switch
        {
            '#' => (Clamp(r + 10), Clamp(g + 10), Clamp(b + 10), (byte)255),
            '+' => (Clamp(r - 14), Clamp(g - 14), Clamp(b - 14), (byte)255),

            // PRŮHLEDNÉ TEXELY NESOU BARVU ROSTLINY, ne černou. Nulová alfa sama nestačí:
            // filtrování i mipmapy míchají RGB sousedních texelů bez ohledu na ni, takže
            // z černého pozadí vzniknou tmavé lemy kolem každého stébla.
            _ => (Clamp(r), Clamp(g), Clamp(b), (byte)0),
        };
    }

    private static (byte R, byte G, byte B, byte A) Blades(
        int x, int y, uint seed, int r, int g, int b, int height, int blades, bool fern = false)
    {
        // Souřadnice od spodní hrany: rostlina roste zdola nahoru.
        int fromBottom = ArtSize - 1 - y;

        if (fromBottom > height)
        {
            return (0, 0, 0, 0);
        }

        (int Root, int Height, int Lean)[] layout = fern ? FernBlades : TuftBlades;
        int count = Math.Min(blades, layout.Length);

        for (int i = 0; i < count; i++)
        {
            (int root, int nominal, int lean) = layout[i];

            // Výšky se přeškálují na požadovanou: rozvržení je napsané pro čtrnáct texelů.
            int bladeHeight = Math.Max(2, nominal * height / 14);

            if (fromBottom > bladeHeight)
            {
                continue;
            }

            // OHYB JE KVADRATICKÝ, ne lineární. U země stéblo stojí, nahoře se překlápí —
            // s přímkou vypadá trs jako vějíř tyček.
            int column = root + (lean * fromBottom * fromBottom / (bladeHeight * bladeHeight));

            // STÉBLO JE VŽDYCKY JEDEN TEXEL ŠIROKÉ.
            //
            // Zkoušel jsem ho dole zdvojit, aby trs nebyl řídký. Dopadlo to opačně:
            // deset stébel po dvou texelech pokrylo u země celou šířku dlaždice, takže
            // spodek byl plná plocha a nahoře z ní trčely jen špičky. Přesně tak to
            // vypadalo i ve hře. Hustotu dělá počet stébel a jejich rozestup, ne šířka.
            if (column != x)
            {
                continue;
            }

            // Špička o něco světlejší, u země tmavší, a každé stéblo má vlastní odstín.
            // Odstín se bere z pořadí stébla, ne z hashe — ať je kresba pokaždé stejná.
            int tip = ((fromBottom * 18) / bladeHeight) - 6;
            int own = ((i * 37) % 15) - 7;

            return (Clamp(r + tip + own), Clamp(g + tip + own), Clamp(b + tip + own), 255);
        }

        _ = seed;
        return (0, 0, 0, 0);
    }

    /// <summary>
    /// Kytka: rovný stonek, dva lístky a nad tím květ.
    ///
    /// <para><b>Kreslí se natvrdo podle řádků, ne z šumu.</b> První verze skládala květ
    /// z <see cref="Blades"/>, tedy z ohnutých stébel obarvených na žluto — vypadalo to
    /// jako rozsypaná sláma, ne jako kytka. Na šestnácti texelech je rostlina tak malá,
    /// že se každý texel počítá a náhoda jen kazí tvar.</para>
    /// </summary>
    private static (byte R, byte G, byte B, byte A) Flower(int x, int y, uint seed, int r, int g, int b)
    {
        // Střed je pevný. Rozmanitost dělá barva květu a natočení celého trsu ve světě,
        // ne posun kresby uvnitř dlaždice.
        const int Center = 7;

        int fromBottom = ArtSize - 1 - y;
        int offset = x - Center;

        // Stonek.
        if (fromBottom <= 8 && offset == 0)
        {
            return Speckled(x, y, seed, 62, 108, 52, 10);
        }

        // Dva lístky, každý na jinou stranu a v jiné výšce.
        if ((fromBottom == 4 && offset == -1) || (fromBottom == 3 && offset == -2)
            || (fromBottom == 6 && offset == 1) || (fromBottom == 5 && offset == 2))
        {
            return Speckled(x, y, seed, 70, 122, 58, 10);
        }

        // Květ. Půlšířka po řádcích dá zaoblený tvar místo kostky.
        int half = fromBottom switch
        {
            9 => 1,
            10 => 2,
            11 => 3,
            12 => 3,
            13 => 2,
            _ => -1,
        };

        if (half < 0 || Math.Abs(offset) > half)
        {
            return (0, 0, 0, 0);
        }

        // Střed květu tmavší, aby nebyl jen barevná skvrna.
        if (fromBottom == 11 && Math.Abs(offset) <= 1)
        {
            return (Clamp((r * 2 / 3) + 20), Clamp((g * 2 / 3) + 10), Clamp(b * 2 / 3), 255);
        }

        int shade = (int)(Hash(x, y, seed) % 20) - 10;
        return (Clamp(r + shade), Clamp(g + shade), Clamp(b + shade), 255);
    }

    /// <summary>Kaktus: svislá žebra se stíny mezi nimi.</summary>
    private static (byte R, byte G, byte B, byte A) CactusSide(int x, int y, uint seed)
    {
        int rib = x % 4 == 0 ? -20 : 0;
        int spine = Hash(x, y, seed) % 23 == 0 ? 40 : 0;

        return Speckled(x, y, seed, 58 + rib + spine, 104 + rib + spine, 54 + rib, 8);
    }

    /// <summary>Štěrk: hrubší zrno než kámen, aby byl na sutích poznat na první pohled.</summary>
    private static (byte R, byte G, byte B, byte A) Gravel(int x, int y, uint seed)
    {
        // Zrno po dvou texelech dělá viditelné kamínky místo jemného šumu.
        uint pebble = Hash(x / 2, y / 2, seed);
        int shade = (int)(pebble % 46) - 23;

        return Speckled(x, y, seed, 124 + shade, 118 + shade, 112 + shade, 6);
    }

    private static (byte R, byte G, byte B, byte A) Planks(int x, int y, uint seed)
    {
        // Vodorovná prkna po čtyřech texelech, svislá spára posunutá po řadách.
        bool gap = y % 4 == 3;
        int plankRow = y / 4;
        bool seam = (x + (plankRow * 5)) % 8 == 0;

        if (gap || seam)
        {
            return Speckled(x, y, seed, 96, 68, 40, 8);
        }

        return Speckled(x, y, seed, 156, 116, 74, 12);
    }

    /// <summary>
    /// Voda: modrá s vlnkami.
    ///
    /// <para>Alfa je vysoká (kolem 200), ne poloprůhledná. Hladina se kreslí v průhledném
    /// průchodu bez zápisu do hloubky, takže při nízké alfě prosvítá i to, co je hluboko
    /// pod ní, a z moře je průsvitná fólie. Vlnky dělá součet dvou sinusovek s různou
    /// vlnovou délkou — jedna sama o sobě vypadá jako pruhy.</para>
    /// </summary>
    private static (byte R, byte G, byte B, byte A) Water(int x, int y, uint seed)
    {
        float wave = MathF.Sin((x + (y * 0.6f)) * 0.9f) + (MathF.Sin((x * 0.35f) - (y * 0.5f)) * 0.6f);
        int shade = (int)(wave * 9f) + ((int)(Hash(x, y, seed) % 7) - 3);

        return (Clamp(44 + shade), Clamp(96 + shade), Clamp(168 + (shade * 2)), 205);
    }

    private static (byte R, byte G, byte B, byte A) Glass(int x, int y)
    {
        bool border = x == 0 || y == 0 || x == ArtSize - 1 || y == ArtSize - 1;
        return border ? ((byte)210, (byte)230, (byte)240, (byte)210) : ((byte)190, (byte)220, (byte)235, (byte)48);
    }

    private static (byte R, byte G, byte B, byte A) Missing(int x, int y)
    {
        bool pink = ((x / 8) + (y / 8)) % 2 == 0;
        return pink ? ((byte)255, (byte)0, (byte)220, (byte)255) : ((byte)0, (byte)0, (byte)0, (byte)255);
    }

    private static byte Clamp(int value) => (byte)Math.Clamp(value, 0, 255);

    private static uint Hash(int x, int y, uint seed)
    {
        uint h = seed ^ ((uint)x * 0x9E3779B1u) ^ ((uint)y * 0x85EBCA77u);
        h ^= h >> 15;
        h *= 0x2545F491u;
        h ^= h >> 13;
        return h;
    }

    private static uint Fnv1a(string text)
    {
        uint hash = 2166136261u;
        foreach (char c in text)
        {
            hash = (hash ^ c) * 16777619u;
        }

        return hash;
    }
}
