using System.Text.Json;

namespace Tesseris.Game.UI;

/// <summary>Hráčská nastavení ukládaná mimo instalaci hry.</summary>
public sealed class PlayerOptions
{
    public const int LowGraphics = 0;
    public const int BalancedGraphics = 1;
    public const int HighGraphics = 2;

    // Rozsahy posuvníků v panelu obrazu. Jsou tady, aby se meze při ukládání a meze
    // v menu nemohly rozejít — obojí čte tahle jména.
    public const float MinExposure = 0.2f;
    public const float MaxExposure = 3f;
    public const float MinSaturation = 0f;
    public const float MaxSaturation = 2.5f;
    public const float MinContrast = 0.5f;
    public const float MaxContrast = 2f;
    public const float MinSplitTone = 0f;
    public const float MaxSplitTone = 2f;
    public const float MinBloomThreshold = 0f;
    public const float MaxBloomThreshold = 2f;
    public const float MinBloomStrength = 0f;
    public const float MaxBloomStrength = 2f;
    public const float MinBloomRadius = 1f;
    public const float MaxBloomRadius = 20f;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public float MouseSensitivity { get; set; } = 0.03f;
    public float FieldOfView { get; set; } = 70f;
    public int ViewDistance { get; set; } = 10;
    public float MasterVolume { get; set; } = 0.7f;
    public bool VSync { get; set; } = true;
    public bool FullView { get; set; }
    public bool AutomaticGraphics { get; set; } = true;
    public bool CustomGraphics { get; set; }
    public int MsaaSamples { get; set; } = 2;
    public bool Fxaa { get; set; } = true;
    public int MapDetailSize { get; set; } = 2048;
    public int CloudQuality { get; set; } = 2;
    public int FogQuality { get; set; } = 2;
    public bool Shadows { get; set; }

    public bool Bloom { get; set; } = true;

    /// <summary>
    /// Odrazivá hladina místo dlaždicové.
    /// </summary>
    /// <remarks>
    /// <para><b>Nejsou to stupně kvality, ale dva vzhledy, které se vylučují.</b> Vypnuto
    /// znamená dlaždici z atlasu, která se vlní a je přes ni vidět dno — vzhled běžný
    /// v blokových hrách. Zapnuto ji nahradí odrazem oblohy a odleskem slunce, čímž
    /// textura hladiny zmizí a pod hladinu vidět není.</para>
    ///
    /// <para>Výchozí je vypnuto, protože dlaždicovou hladinu chce zadavatel. Zapnutím
    /// se textura ztratí — to není chyba, to je ten druhý vzhled.</para>
    ///
    /// <para>Dřív to bylo jen v rendereru bez počáteční hodnoty, takže se přepnutí v menu
    /// po restartu vždycky zapomnělo. Proto to je tady, mezi ukládanými nastaveními.</para>
    /// </remarks>
    public bool FancyWater { get; set; }

    /// <summary>Kolik je v noci vidět. Viz DayCycle.NightBrightness.</summary>
    public float NightBrightness { get; set; } = 0.22f;

    /// <summary>Co znamená plný noční jas. Viz DayCycle.NightBrightnessCeiling.</summary>
    public float NightBrightnessCeiling { get; set; } = 1f;

    // ===== OBRAZ A BARVY (panel F9) =====
    //
    // Ukládá se to proto, že jinak by se každé ladění zahodilo zavřením hry a barvy by se
    // musely nastavovat znovu při každém spuštění. Výchozí hodnoty jsou tytéž jako
    // v ChunkRendereru; ten je zdroj pravdy, tohle je jejich uložená podoba.

    /// <summary>Filmová křivka, sytost a kontrast. Vypnutím se scéna vykreslí bez korekce.</summary>
    public bool ColorGrading { get; set; } = true;

    /// <summary>Násobek jasu před filmovou křivkou. Viz ChunkRenderer.Exposure.</summary>
    public float Exposure { get; set; } = 1.05f;

    /// <summary>Sytost barev. Viz ChunkRenderer.ColorSaturation.</summary>
    public float ColorSaturation { get; set; } = 1.28f;

    /// <summary>Kontrast. Viz ChunkRenderer.ColorContrast.</summary>
    public float ColorContrast { get; set; } = 1.16f;

    /// <summary>Teplo ve světlech a modrá ve stínech. Viz ChunkRenderer.SplitTone.</summary>
    public float SplitTone { get; set; } = 0.55f;

    /// <summary>Od jakého jasu se přičítá záře. Viz ChunkRenderer.BloomThreshold.</summary>
    public float BloomThreshold { get; set; } = 1.0f;

    /// <summary>Jak silně se záře přičte. Viz ChunkRenderer.BloomStrength.</summary>
    public float BloomStrength { get; set; } = 0.40f;

    /// <summary>Dosah rozmazání záře v pixelech. Viz ChunkRenderer.BloomRadius.</summary>
    public float BloomRadius { get; set; } = 3.0f;
    /// <summary>
    /// -1 marks an options file written before graphics profiles existed. It is migrated to
    /// Balanced on load so an old view-distance 16 file does not silently retain Ultra-like costs.
    /// </summary>
    public int GraphicsQuality { get; set; } = -1;

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Tesseris",
        "options.json");

    public static PlayerOptions Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            PlayerOptions value = File.Exists(path)
                ? JsonSerializer.Deserialize<PlayerOptions>(File.ReadAllText(path), JsonOptions) ?? new PlayerOptions()
                : new PlayerOptions();
            value.Clamp();
            return value;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            var fallback = new PlayerOptions();
            fallback.Clamp();
            return fallback;
        }
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        Clamp();
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(temporary, path, overwrite: true);
    }

    public void Clamp()
    {
        if (GraphicsQuality < LowGraphics || GraphicsQuality > HighGraphics)
        {
            GraphicsQuality = BalancedGraphics;
            ViewDistance = 10;
        }

        MouseSensitivity = Math.Clamp(MouseSensitivity, 0.005f, 0.3f);
        FieldOfView = Math.Clamp(FieldOfView, 50f, 110f);
        ViewDistance = Math.Clamp(ViewDistance, 2, 32);
        MasterVolume = Math.Clamp(MasterVolume, 0f, 1f);
        MsaaSamples = MsaaSamples switch { <= 1 => 1, 2 => 2, _ => 4 };
        MapDetailSize = MapDetailSize switch { <= 1024 => 1024, <= 2048 => 2048, _ => 4096 };
        NightBrightness = Math.Clamp(NightBrightness, 0f, 1f);
        NightBrightnessCeiling = Math.Clamp(NightBrightnessCeiling, 0.05f, 1f);

        // Meze musí sedět na rozsahy posuvníků v panelu F9. Kdyby byly širší, dal by se
        // sem uložit stav, na který se posuvníkem nedá vrátit.
        Exposure = Math.Clamp(Exposure, MinExposure, MaxExposure);
        ColorSaturation = Math.Clamp(ColorSaturation, MinSaturation, MaxSaturation);
        ColorContrast = Math.Clamp(ColorContrast, MinContrast, MaxContrast);
        SplitTone = Math.Clamp(SplitTone, MinSplitTone, MaxSplitTone);
        BloomThreshold = Math.Clamp(BloomThreshold, MinBloomThreshold, MaxBloomThreshold);
        BloomStrength = Math.Clamp(BloomStrength, MinBloomStrength, MaxBloomStrength);
        BloomRadius = Math.Clamp(BloomRadius, MinBloomRadius, MaxBloomRadius);
        CloudQuality = Math.Clamp(CloudQuality, 0, 3);
        FogQuality = Math.Clamp(FogQuality, 0, 3);
    }

    public void ApplyGraphicsProfile(int quality, bool automatic)
    {
        GraphicsQuality = Math.Clamp(quality, LowGraphics, HighGraphics);
        AutomaticGraphics = automatic;
        CustomGraphics = false;

        switch (GraphicsQuality)
        {
            case LowGraphics:
                ViewDistance = 8;
                MsaaSamples = 1;
                Fxaa = false;
                MapDetailSize = 1024;
                Bloom = Bloom;
                CloudQuality = 1;
                FogQuality = 1;
                Bloom = false;
                break;
            case HighGraphics:
                ViewDistance = 16;
                MsaaSamples = 4;
                Fxaa = true;
                MapDetailSize = 4096;
                Bloom = Bloom;
                CloudQuality = 3;
                FogQuality = 3;
                Bloom = true;
                break;
            default:
                ViewDistance = 10;
                MsaaSamples = 2;
                Fxaa = true;
                MapDetailSize = 2048;
                Bloom = Bloom;
                CloudQuality = 2;
                FogQuality = 2;
                Bloom = true;
                break;
        }
    }

    public static int RecommendGraphicsProfile(ulong deviceLocalMemoryBytes, bool portability)
    {
        double memoryGiB = deviceLocalMemoryBytes / (1024.0 * 1024.0 * 1024.0);
        if (portability)
        {
            return memoryGiB < 7.0 ? LowGraphics : BalancedGraphics;
        }

        // OSM GIGABAJTŮ NA NEJVYŠŠÍ PROFIL, ne šestnáct. Šestnáct byla laťka, kterou
        // nesplní ani RTX 4070 SUPER s dvanácti — a ta má na nejvyšší profil se vším všudy
        // výkonu víc než dost. Naměřeno na ní: 446 snímků za vteřinu, grafika čekala
        // 0,04 ms na snímek. Karty s osmi a víc gigabajty jsou dnes běžný střed pole.
        return memoryGiB <= 4.5
            ? LowGraphics
            : memoryGiB >= 8.0
                ? HighGraphics
                : BalancedGraphics;
    }
}
