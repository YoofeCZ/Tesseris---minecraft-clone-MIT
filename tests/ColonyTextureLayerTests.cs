using System.Text.RegularExpressions;
using Tesseris.Engine.Rendering;
using Tesseris.Game.Blocks;
using Tesseris.Game.Colony;
using Xunit;

namespace Tesseris.Tests;

/// <summary>
/// Testy vrstev atlasu pro pás, stroj, vkládač a item na pásu.
/// </summary>
/// <remarks>
/// <para><b>Proč tenhle soubor existuje.</b> Vrstvy atlasu jsou tříděné podle jména, takže
/// natvrdo psaný index se rozejde s obsahem, jakmile někdo přidá blok. V tomhle projektu to
/// skončilo čtyřikrát tím, že se něco kreslilo jako kůra, obličej, listí nebo prkna.
/// Naposledy tady: 5, 7, 9 a 11 vycházelo na <c>acacia_planks</c>, <c>ancient_tech_panel</c>,
/// <c>basalt</c> a <c>birch_leaves_02</c>.</para>
///
/// <para><b>Co tyhle testy hlídají.</b> Nejen že jsou dnešní vrstvy správné — to by se dalo
/// splnit i výměnou čísel. Hlídají, že se vrstvy vážou na JMÉNO, že se chybějící vrstva
/// nerozsvítí jako nula, že item na pásu není z poloviny průhledný a že se do kreslení
/// kolonie nevrátí číselný literál.</para>
/// </remarks>
public sealed class ColonyTextureLayerTests
{
    private static readonly string Assets = Path.Combine(AppContext.BaseDirectory, "assets");

    private static readonly string WindowSource = FindSource("src", "Game", "TesserisWindow.cs");

    /// <summary>
    /// Vrstvy se váží na jméno textury, ne na pořadí v atlasu.
    /// </summary>
    /// <remarks>
    /// Rozhodovací test celé opravy: seznam vrstev se posune o kus dopředu a vrstvy se musí
    /// posunout s ním. Natvrdo psané číslo tímhle projít nemůže — zůstalo by, kde bylo.
    /// </remarks>
    [Fact]
    public void Vrstvy_se_vazou_na_jmeno_a_posun_atlasu_je_posune_taky()
    {
        List<string> atlas = [.. ColonyTextureLayers.Names];
        ColonyTextureLayers before = ColonyTextureLayers.Resolve(atlas.IndexOf);

        // Přesně to, co se v téhle codebase děje pokaždé, když někdo přidá blok: obsah se
        // před ně vloží a všechno se posune.
        atlas.InsertRange(0, ["aaa_novy_blok", "aab_dalsi_blok", "aac_treti_blok"]);
        ColonyTextureLayers after = ColonyTextureLayers.Resolve(atlas.IndexOf);

        Assert.Equal(before.Belt + 3f, after.Belt);
        Assert.Equal(before.Machine + 3f, after.Machine);
        Assert.Equal(before.Inserter + 3f, after.Inserter);
        Assert.Equal(before.BeltItem + 3f, after.BeltItem);

        // A pořád ukazují na svoje jméno, ne na cizí texturu.
        Assert.Equal(ColonyTextureLayers.BeltTexture, atlas[(int)after.Belt]);
        Assert.Equal(ColonyTextureLayers.MachineTexture, atlas[(int)after.Machine]);
        Assert.Equal(ColonyTextureLayers.InserterTexture, atlas[(int)after.Inserter]);
        Assert.Equal(ColonyTextureLayers.BeltItemTexture, atlas[(int)after.BeltItem]);
    }

    /// <summary>
    /// Nepřihlášená vrstva zůstane na −1, ne na nule.
    /// </summary>
    /// <remarks>
    /// Nula je platný index — v dnešním atlasu <c>acacia_leaves</c>. Kdyby se chybějící
    /// vrstva dosadila nulou, kreslilo by se listí a vypadalo by to jako záměr. Je to totéž
    /// rozhodnutí jako u <c>ChunkRenderer.PlayerSkinLayer</c>.
    /// </remarks>
    [Fact]
    public void Chybejici_vrstva_je_minus_jedna_a_nikdy_nula()
    {
        ColonyTextureLayers prazdny = ColonyTextureLayers.Resolve(_ => -1);

        Assert.Equal(-1f, prazdny.Belt);
        Assert.Equal(-1f, prazdny.Machine);
        Assert.Equal(-1f, prazdny.Inserter);
        Assert.Equal(-1f, prazdny.BeltItem);
        Assert.False(prazdny.IsComplete);
        Assert.Equal(ColonyTextureLayers.Names, prazdny.MissingNames());

        // Výchozí hodnota bez jakéhokoli nastavení je taky −1, ne nula. Přesně tahle mezera
        // pustila kolonisty do světa zelené.
        var vychozi = default(ColonyTextureLayers);
        Assert.Equal(-1f, vychozi.Belt);
        Assert.Equal(-1f, vychozi.Machine);
        Assert.Equal(-1f, vychozi.Inserter);
        Assert.Equal(-1f, vychozi.BeltItem);
        Assert.False(vychozi.IsComplete);
    }

    /// <summary>Chybí-li jen jedna vrstva, pozná se která — a celek není hotový.</summary>
    [Fact]
    public void Chybejici_vrstvu_umi_pojmenovat()
    {
        List<string> atlas = [.. ColonyTextureLayers.Names];
        atlas.Remove(ColonyTextureLayers.BeltItemTexture);

        ColonyTextureLayers layers = ColonyTextureLayers.Resolve(atlas.IndexOf);

        Assert.False(layers.IsComplete);
        Assert.Equal([ColonyTextureLayers.BeltItemTexture], layers.MissingNames());
        Assert.True(layers.Belt >= 0f);
        Assert.True(layers.Machine >= 0f);
        Assert.True(layers.Inserter >= 0f);
        Assert.Equal(-1f, layers.BeltItem);
    }

    /// <summary>
    /// Item na pásu musí být neprůhledný, jinak ho výřezový shader zahodí.
    /// </summary>
    /// <remarks>
    /// <para>Jádro celé chyby. Item má hranu 0,3 bloku a kreslí se
    /// <c>item_instanced.frag</c>, který zahazuje texely s alfou pod 0,5. Předtím tu bylo
    /// <c>birch_leaves_02</c> s 2 612 ze 4 096 texelů pod prahem, takže z 28 metrů, odkud se
    /// dívá velitel, po itemu nezbylo skoro nic.</para>
    ///
    /// <para>Práh je 5 %, ne nula: menší okrajová průhlednost siluetu nerozbije, ale
    /// polovina obrázku ano.</para>
    /// </remarks>
    [Fact]
    public void Item_na_pasu_neni_z_poloviny_pruhledny()
    {
        (int Below, int Total) alpha = AlphaBelowThreshold(ColonyTextureLayers.BeltItemTexture);

        double share = alpha.Below / (double)alpha.Total;
        Assert.True(
            share <= 0.05,
            $"Textura itemu na pásu '{ColonyTextureLayers.BeltItemTexture}' má "
            + $"{alpha.Below} z {alpha.Total} texelů pod prahem výřezu ({share:P1}). "
            + "Výřezový shader je zahodí a item na pásu nebude z dálky vidět — přesně tohle "
            + "dělalo birch_leaves_02 s 2 612 ze 4 096.");
    }

    /// <summary>
    /// Pás, stroj a vkládač mají souvislé těleso, ne poloprůhlednou kresbu.
    /// </summary>
    /// <remarks>
    /// Volnější práh než u itemu na pásu: tyhle tři jsou velké (0,45 až 0,9 bloku) a strojní
    /// textury mají průhledný okraj kolem obrysu. <c>hypro_ore_crusher</c> má 14,7 % texelů
    /// pod prahem, ale je to lem — prostřední čtverec 64×64 má jen 1,3 %.
    /// </remarks>
    [Fact]
    public void Pas_stroj_i_vkladac_maji_souvisle_teleso()
    {
        foreach (string texture in new[]
        {
            ColonyTextureLayers.BeltTexture,
            ColonyTextureLayers.MachineTexture,
            ColonyTextureLayers.InserterTexture,
        })
        {
            (int Below, int Total) alpha = AlphaBelowThreshold(texture);
            double share = alpha.Below / (double)alpha.Total;

            Assert.True(
                share <= 0.25,
                $"Textura '{texture}' má {share:P1} texelů pod prahem výřezu; "
                + "těleso by bylo děravé.");
        }
    }

    /// <summary>Každá role má svou vlastní texturu — jinak se od sebe nepoznají.</summary>
    [Fact]
    public void Kazda_role_ma_vlastni_texturu()
    {
        Assert.Equal(4, ColonyTextureLayers.Names.Count);
        Assert.Equal(
            ColonyTextureLayers.Names.Count,
            ColonyTextureLayers.Names.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// Textury kolonie nejsou listí ani prkna, tedy nic, co roste nebo se staví.
    /// </summary>
    /// <remarks>
    /// Hráč musí pás a stroj poznat od terénu. Tenhle test chytí návrat k texturám, na které
    /// ta čtyři čísla vycházela, i kdyby se do vrstev dostaly jinou cestou.
    /// </remarks>
    [Fact]
    public void Textury_kolonie_nejsou_teren_ani_rostliny()
    {
        string[] zakazane =
        [
            "acacia_planks", "ancient_tech_panel", "basalt", "birch_leaves_02",
        ];

        foreach (string name in ColonyTextureLayers.Names)
        {
            Assert.DoesNotContain(name, zakazane, StringComparer.Ordinal);
            Assert.DoesNotContain("leaves", name, StringComparison.Ordinal);
            Assert.DoesNotContain("_log_", name, StringComparison.Ordinal);
            Assert.DoesNotContain("sapling", name, StringComparison.Ordinal);
        }
    }

    /// <summary>Všechny textury kolonie doopravdy existují a atlas je zná.</summary>
    /// <remarks>
    /// <para><b>Tohle není formalita.</b> Atlas na neznámé jméno nespadne — vyrobí náhradní
    /// dlaždici a <b>uloží si ji na disk</b> vedle binárky. Vrstva se pak „najde" a kontrola
    /// chybějící vrstvy už nikdy nic nenajde.</para>
    ///
    /// <para>Ověřeno sondou: s vymyšleným jménem vyšla ve hře vrstva 212 a hlášení mlčelo.
    /// Teprve když začala registrace ověřovat existenci souboru, ohlásila hra
    /// <c>item -1 CHYBI</c> a instancí ubylo z 20 na 15.</para>
    /// </remarks>
    [Fact]
    public void Textury_kolonie_existuji_v_assetech()
    {
        foreach (string name in ColonyTextureLayers.Names)
        {
            string path = Path.Combine(Assets, "textures", $"{name}.png");
            Assert.True(File.Exists(path), $"Chybí textura {path}.");
        }
    }

    /// <summary>
    /// Vrstva se přihlašuje jen tehdy, když textura opravdu existuje.
    /// </summary>
    /// <remarks>
    /// Doplněk předchozího testu, který hlídá <b>kód</b>, ne obsah. Registrace u atlasu musí
    /// existenci souboru ověřit sama; kdyby jména přidávala naslepo, byla by kontrola
    /// chybějící vrstvy mrtvý kód a chyba by se zase projevila až očima ve hře.
    /// </remarks>
    [Fact]
    public void Do_atlasu_se_hlasi_jen_existujici_textury()
    {
        string source = File.ReadAllText(WindowSource);

        int start = source.IndexOf(
            "foreach (string name in ColonyTextureLayers.Names)", StringComparison.Ordinal);
        Assert.True(start >= 0, "Zmizela registrace vrstev kolonie do atlasu.");

        string body = source[start..Math.Min(source.Length, start + 1200)];

        Assert.Contains("ResolveTexturePath", body, StringComparison.Ordinal);
        Assert.Contains("File.Exists", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// Vrstvy se v běžícím atlasu opravdu najdou.
    /// </summary>
    /// <remarks>
    /// Skládá se tu týž seznam jako v <c>TesserisWindow</c>: nejdřív jména z registry bloků,
    /// pak jména kolonie. Test tím ověří, že se <c>Resolve</c> na skutečném obsahu chytí —
    /// ne jen na vymyšleném seznamu.
    /// </remarks>
    [Fact]
    public void Vrstvy_se_najdou_v_atlasu_slozenem_ze_skutecnych_bloku()
    {
        BlockRegistry blocks = BlockRegistry.LoadFromDirectory(Path.Combine(Assets, "blocks"));
        List<string> atlas = [.. blocks.TextureNames];

        foreach (string name in ColonyTextureLayers.Names)
        {
            if (!atlas.Contains(name))
            {
                atlas.Add(name);
            }
        }

        ColonyTextureLayers layers = ColonyTextureLayers.Resolve(atlas.IndexOf);

        Assert.True(layers.IsComplete, $"Nenašly se: {string.Join(", ", layers.MissingNames())}.");
        Assert.Equal(ColonyTextureLayers.BeltTexture, atlas[(int)layers.Belt]);
        Assert.Equal(ColonyTextureLayers.MachineTexture, atlas[(int)layers.Machine]);
        Assert.Equal(ColonyTextureLayers.InserterTexture, atlas[(int)layers.Inserter]);
        Assert.Equal(ColonyTextureLayers.BeltItemTexture, atlas[(int)layers.BeltItem]);
    }

    /// <summary>
    /// V kreslení kolonie není číselný literál vrstvy.
    /// </summary>
    /// <remarks>
    /// <para>Tenhle test nekontroluje chování, ale zdroják — schválně. Ostatní testy projdou
    /// i tehdy, když někdo napíše <c>AddInstancedItem(..., 5f, ...)</c> vedle
    /// <see cref="ColonyTextureLayers"/>, protože o té nové cestě nevědí.</para>
    ///
    /// <para>Je to popáté tatáž chyba, co by se tím vracela, takže stojí za to hlídat i tvar
    /// kódu. Vrstva je čtvrtý argument <c>AddInstancedItem</c>.</para>
    /// </remarks>
    [Fact]
    public void Kresleni_kolonie_nepouziva_ciselny_literal_vrstvy()
    {
        string source = File.ReadAllText(WindowSource);

        int start = source.IndexOf("private void DrawColonyInstances()", StringComparison.Ordinal);
        Assert.True(start >= 0, "V TesserisWindow.cs zmizela metoda DrawColonyInstances.");

        string body = MethodBody(source, start);

        // Čtvrtý argument AddInstancedItem je vrstva. Literál na jeho místě je návrat
        // přesně té chyby, kvůli které tenhle soubor vznikl.
        var calls = new Regex(
            @"AddInstancedItem(?:Unculled)?\s*\((?<args>[^;]*?)\)\s*;",
            RegexOptions.Singleline);

        int checkedCalls = 0;

        foreach (Match call in calls.Matches(body))
        {
            string[] args = SplitArguments(call.Groups["args"].Value);
            Assert.True(args.Length >= 4, $"Nečekaný tvar volání: {call.Value}");

            // Volání s frustem má o argument víc; vrstva je vždycky předposlední.
            string layer = args[^2].Trim();
            checkedCalls++;

            Assert.False(
                float.TryParse(
                    layer.TrimEnd('f', 'F'),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out _),
                $"Vrstva '{layer}' je natvrdo psané číslo. Atlas je tříděný podle jména, "
                + "takže se to rozejde s obsahem při prvním přidaném bloku. "
                + "Použij ColonyTextureLayers.");

            Assert.Contains("_colonyLayers.", layer, StringComparison.Ordinal);
        }

        Assert.True(checkedCalls >= 4, $"Zkontrolovalo se jen {checkedCalls} volání ze čtyř.");
    }

    /// <summary>
    /// V okně nezůstala natvrdo psaná pole vrstev kolonie.
    /// </summary>
    /// <remarks>
    /// Doplněk předchozího testu: ten hlídá místo použití, tenhle místo vzniku. Bez něj by
    /// šlo číslo schovat do pole a pořád ho do kreslení podstrčit pod jménem proměnné.
    /// </remarks>
    [Fact]
    public void V_okne_nezustala_natvrdo_psana_pole_vrstev()
    {
        string source = File.ReadAllText(WindowSource);

        foreach (string field in new[]
        {
            "_beltLayer", "_machineLayer", "_inserterLayer", "_beltItemLayer",
        })
        {
            var assignment = new Regex($@"{field}\s*=\s*-?\d+(\.\d+)?f");
            Assert.False(
                assignment.IsMatch(source),
                $"Pole {field} má zase natvrdo psanou vrstvu. Použij ColonyTextureLayers.");
        }
    }

    /// <summary>Kolik texelů textury je pod prahem, který výřezový shader zahazuje.</summary>
    /// <remarks>
    /// Práh 0,5 sedí s <c>item_instanced.frag</c>, kde je <c>if (texel.a &lt; 0.5) discard;</c>.
    /// Čte se skutečný PNG z assetů, ne popis nebo metadata — o průhlednosti rozhodují pixely.
    /// </remarks>
    private static (int Below, int Total) AlphaBelowThreshold(string texture)
    {
        string path = Path.Combine(Assets, "textures", $"{texture}.png");
        Assert.True(File.Exists(path), $"Chybí textura {path}.");

        using FileStream stream = File.OpenRead(path);
        StbImageSharp.ImageResult image =
            StbImageSharp.ImageResult.FromStream(stream, StbImageSharp.ColorComponents.RedGreenBlueAlpha);

        int below = 0;
        int total = image.Width * image.Height;

        for (int i = 0; i < total; i++)
        {
            if (image.Data[(i * 4) + 3] < 128)
            {
                below++;
            }
        }

        return (below, total);
    }

    /// <summary>Tělo metody od její otevírací závorky po odpovídající zavírací.</summary>
    private static string MethodBody(string source, int start)
    {
        int open = source.IndexOf('{', start);
        Assert.True(open >= 0, "Metoda nemá tělo.");

        int depth = 0;

        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{')
            {
                depth++;
            }
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return source[open..i];
                }
            }
        }

        Assert.Fail("Metodě chybí zavírací závorka.");
        return string.Empty;
    }

    /// <summary>Rozdělí argumenty volání čárkami, které nejsou uvnitř závorek.</summary>
    private static string[] SplitArguments(string arguments)
    {
        var parts = new List<string>();
        int depth = 0;
        int last = 0;

        for (int i = 0; i < arguments.Length; i++)
        {
            char c = arguments[i];

            if (c is '(' or '[')
            {
                depth++;
            }
            else if (c is ')' or ']')
            {
                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                parts.Add(arguments[last..i]);
                last = i + 1;
            }
        }

        parts.Add(arguments[last..]);
        return [.. parts];
    }

    /// <summary>
    /// Najde soubor ve stromu repozitáře.
    /// </summary>
    /// <remarks>
    /// Testy běží z <c>bin</c>, takže se ke zdrojáku musí vyšplhat nahoru. Hledá se kořen
    /// podle <c>Tesseris.sln</c>, ne pevným počtem <c>..</c> — ten by se rozešel při první
    /// změně cílové složky.
    /// </remarks>
    private static string FindSource(params string[] relative)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Tesseris.sln")))
            {
                string path = Path.Combine([directory.FullName, .. relative]);
                Assert.True(File.Exists(path), $"Nenašel se zdroják {path}.");
                return path;
            }

            directory = directory.Parent;
        }

        Assert.Fail("Nenašel se kořen repozitáře (Tesseris.sln).");
        return string.Empty;
    }
}
