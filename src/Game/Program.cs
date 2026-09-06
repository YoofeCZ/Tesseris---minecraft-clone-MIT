using System.Globalization;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using Tesseris.Engine.Core;
using Tesseris.Engine.Rendering.Vulkan;

namespace Tesseris.Game;

public static class Program
{
    /// <summary>Rozpočet na frame odpovídající 60 FPS.</summary>
    private const double FrameBudgetMs = 1000.0 / 60.0;

    public static int Main(string[] args)
    {
        if (!TryParseSelftestFrames(args, out int selftestFrames, out string? error))
        {
            Console.Error.WriteLine(error);
            Console.Error.WriteLine("Použití: Tesseris [--selftest=N]");
            return 1;
        }

        // LOADER MUSÍ BÝT PŘED OKNEM. GLFW se při zakládání okna ptá, jestli je Vulkan
        // k dispozici — kdyby se loader nastavoval až za tím, přišlo by to pozdě
        // a GLFW by Vulkan nenašlo. Přesně na tohle port narazil.
        //
        // Validace jen v selftestu nebo na vyžádání. Ve hře je to brzda: kontroluje každé
        // volání Vulkanu a při tisících draw callů na snímek to stojí desítky milisekund.
        string? validationEnv = Environment.GetEnvironmentVariable("TESSERIS_VALIDATION");
        VulkanDebug.Requested = validationEnv is null ? selftestFrames > 0 : validationEnv == "1";

        try
        {
            VulkanLoader.Initialize();
        }
        catch (PlatformNotSupportedException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }

        // O VÝBĚR GRAFIKY SE STARÁ VULKAN SÁM. Zařízení si vyjmenuje a vybere z nich —
        // na rozdíl od OpenGL, kterému systém přidělí jedno a nedá se to ovlivnit jinak
        // než předvolbou ve Windows.

        var gameSettings = new GameWindowSettings
        {
            // BEZ STROPU. O tempo se stará vsync, tedy přepínač v nastavení — viz
            // TesserisWindow.ApplyVsync.
            //
            // <b>Dřív tu strop na obnovovací frekvenci monitoru byl</b> a měl dobrý důvod:
            // vulkanská prezentace jela v režimu MAILBOX, který při obnově vzal nejnovější
            // hotový snímek a ostatní zahodil. Zapnutý vsync tím pádem nic nezdržel, každý
            // zobrazený snímek byl jinak starý a při otáčení myší to bylo vidět jako
            // poskakování. Strop na procesoru byl jediný způsob, jak pohyb srovnat.
            //
            // <b>V OpenGL to neplatí.</b> Zapnutý vsync znamená interval prohození 1
            // a prohození samo počká na monitor, takže se snímky rovnají bez cizí pomoci.
            // Strop navíc přebíjel přepínač: hra držela obnovovací frekvenci monitoru
            // i s vypnutým vsyncem, takže vypnout se fakticky nedal.
            UpdateFrequency = 0.0,
        };

        var nativeSettings = new NativeWindowSettings
        {
            ClientSize = new Vector2i(1280, 720),
            Title = "Tesseris",

            // BEZ GRAFICKÉHO KONTEXTU. Kdyby se tu nechal ContextAPI.OpenGL, GLFW by vedle
            // Vulkanu zakládalo ještě kontext OpenGL — zbytečný a na některých ovladačích
            // konfliktní. Vulkan si surface vyrobí sám přes VulkanSurfaceProvider.
            API = ContextAPI.NoAPI,

            StartVisible = true,
            StartFocused = true,
        };

        using var watchdog = selftestFrames > 0 ? StartWatchdog(selftestFrames) : null;

        // Vsync je VYPNUTÝ i ve hře.
        //
        // Dřív byl zapnutý s odůvodněním „nemá smysl pálit GPU na tisíce snímků". Jenže se
        // zapnutým vsyncem ukazuje overlay frekvenci monitoru (tady kolem 161 Hz), ne to,
        // co engine zvládne — a nešlo tak poznat, jestli je na tom hra dobře, nebo špatně.
        // Zadavatel si vypnutí vyžádal právě proto.
        //
        // Zapnout se dá kdykoli v dev menu (F4), řádek „Vsync (ceka na monitor)".
        // Nenastavuje se přes GameWindow.VSync — to sahá na grafický kontext, který okno
        // bez API nemá. Ve Vulkanu o tom rozhoduje režim zobrazování swapchainu.
        // VE HŘE ZAPNUTÝ, V SELFTESTU VYPNUTÝ.
        //
        // Vypnutý byl původně i ve hře, aby overlay ukazoval, co engine zvládne, a ne
        // frekvenci monitoru. Cena se ale ukázala v praxi: bez čekání se karta žene na
        // tisíce snímků za vteřinu, hřeje se a v tichém pokoji je slyšet — u scény, která
        // se stejně zobrazí jen stošedesátkrát za vteřinu.
        //
        // Kdo chce vidět strop výkonu, vypne si vsync v dev menu (F4) na řádku
        // „Vsync (ceka na monitor)". Selftest ho má vypnutý vždycky, protože měří engine.
        using var window = new TesserisWindow(
            gameSettings, nativeSettings, selftestFrames, vsync: selftestFrames == 0);

        window.Run();

        return selftestFrames > 0 ? ReportSelftest(window, selftestFrames) : 0;
    }


    /// <summary>
    /// Vytáhne z argumentů počet framů pro selftest. Bez přepínače vrací 0, tedy běžný běh.
    /// Oddělené a veřejné kvůli testům.
    /// </summary>
    internal static bool TryParseSelftestFrames(string[] args, out int frames, out string? error)
    {
        ArgumentNullException.ThrowIfNull(args);

        frames = 0;
        error = null;

        foreach (string arg in args)
        {
            const string prefix = "--selftest=";
            if (!arg.StartsWith(prefix, StringComparison.Ordinal))
            {
                error = $"Neznámý argument: {arg}";
                return false;
            }

            string value = arg[prefix.Length..];
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) || parsed <= 0)
            {
                error = $"Počet framů musí být kladné celé číslo, dostal jsem: '{value}'";
                return false;
            }

            frames = parsed;
        }

        return true;
    }

    /// <summary>
    /// Hlídač, který proces tvrdě ukončí, kdyby se smyčka zasekla. Bez něj by zaseknuté okno
    /// v neinteraktivním běhu viselo, dokud ho někdo nezabije.
    /// </summary>
    private static Timer StartWatchdog(int frames)
    {
        // Štědrý rozpočet: studený start s kompilací shaderů zabere přes sekundu,
        // zbytek je 60 FPS strop s velkou rezervou.
        TimeSpan limit = TimeSpan.FromSeconds(20) + TimeSpan.FromSeconds(frames / 60.0);

        return new Timer(
            _ =>
            {
                Console.Error.WriteLine($"SELFTEST TIMEOUT: smyčka nedoběhla do {limit.TotalSeconds:F0} s.");
                Environment.Exit(1);
            },
            state: null,
            dueTime: limit,
            period: Timeout.InfiniteTimeSpan);
    }

    private static int ReportSelftest(TesserisWindow window, int requestedFrames)
    {
        FrameStats frame = window.Timing.TotalStats(FrameBudgetMs);
        FrameStats cpu = window.Timing.CpuStats(FrameBudgetMs);

        Console.WriteLine();
        Console.WriteLine("=== SELFTEST ===");
        Console.WriteLine(Line($"Vyžádáno framů      : {requestedFrames}"));
        Console.WriteLine(Line($"Odměřeno vzorků     : {frame.SampleCount}"));
        Console.WriteLine(Line($"Chyby ovladace      : {VulkanDebug.ErrorCount}"));
        Console.WriteLine(Line($"Zařízení            : {window.GpuName}"));
        Console.WriteLine(Line($"Rozlišení           : {window.RenderResolution}"));
        Console.WriteLine(Line($"Paměťové alokace    : {window.BufferAllocations}"));
        Console.WriteLine();
        Console.WriteLine("Čas framu při vypnutém vsyncu (tohle je metrika pro >=60 FPS):");
        Console.WriteLine(Line($"  p50 / p95 / p99   : {frame.P50Ms:F3} / {frame.P95Ms:F3} / {frame.P99Ms:F3} ms"));
        Console.WriteLine(Line($"  min / max         : {frame.MinMs:F3} / {frame.MaxMs:F3} ms"));
        Console.WriteLine(Line($"  nad rozpočtem     : {frame.OverBudget} z {frame.SampleCount} (rozpočet {FrameBudgetMs:F2} ms)"));
        Console.WriteLine();
        Console.WriteLine("Čas hlavního vlákna (diagnostika CPU vs GPU, NE důkaz snímkové frekvence):");
        Console.WriteLine(Line($"  p50 / p99 / max   : {cpu.P50Ms:F3} / {cpu.P99Ms:F3} / {cpu.MaxMs:F3} ms"));
        Console.WriteLine(Line($"  podíl na nejhorším framu: {(frame.MaxMs > 0 ? cpu.MaxMs / frame.MaxMs * 100.0 : 0.0):F0} %"));

        Console.WriteLine();
        Console.WriteLine("Simulační tick (pevných 60 Hz, oddělený od renderu):");
        FrameStats tick = window.TickStats;
        Console.WriteLine(Line($"  p50 / p99 / max   : {tick.P50Ms:F3} / {tick.P99Ms:F3} / {tick.MaxMs:F3} ms (cíl p99 < 8 ms)"));
        // P50 I P99. Dřív se tiskl jen medián a čtenář ho porovnával s p99 celku o řádek
        // výš — tedy medián proti špičce. Vypadalo to, že se v tiku ztrácí sedmnáct
        // milisekund, které nikde nechyběly.
        foreach ((string name, double p50, double p99) in window.TickBreakdown())
        {
            Console.WriteLine(Line($"    {name,-12}: p50 {p50:F3} / p99 {p99:F3} ms"));
        }

        Console.WriteLine();
        Console.WriteLine("Kolonie (T2/T3/T5):");
        Console.WriteLine(Line($"  {window.ColonyStatus}"));
        Console.WriteLine();
        Console.WriteLine("Instancované itemy (T4):");
        Console.WriteLine(Line($"  kresleno / mimo zaber: {window.InstancedItems} / {window.InstancedItemsCulled}"));
        Console.WriteLine(Line($"  nahrani instanci  : {window.InstancedItemsUploadMs:F3} ms"));

        Console.WriteLine();
        Console.WriteLine("Svět a streaming:");
        Console.WriteLine(Line($"  dohled            : {window.ViewDistanceChunks} chunků, {window.WorkerCount} worker vláken"));
        Console.WriteLine(Line($"  nalétaná dráha    : {window.FlightDistance:F0} bloků ({window.FlightDistance / 32f:F1} chunků)"));
        Console.WriteLine(Line($"  chunků ve světě   : {window.LoadedChunks}, s geometrií {window.RenderedChunks}"));
        Console.WriteLine(Line($"  viditelných       : {window.VisibleChunks}, po otočení kamery {window.VisibleChunksLookingAway}"));
        Console.WriteLine(Line($"  trojúhelníků      : {window.TriangleCount}"));
        Console.WriteLine(Line($"  meshing po zahřátí: {window.MeshSteadyStateWorstMs:F3} ms na chunk (cíl < 2 ms)"));
        Console.WriteLine(Line($"  raycast           : {window.RaycastResult}"));
        Console.WriteLine(Line($"  zkušební úprava   : {window.EditProbe}"));
        Console.WriteLine(Line($"  trojúhelníků před/po: {window.TrianglesBeforeEdit} / {window.TrianglesAfterEdit}"));
        Console.WriteLine();
        Console.WriteLine("Mikrovoxely:");
        Console.WriteLine($"  {window.ChiselProbe}");
        Console.WriteLine();
        Console.WriteLine("Zvuk:");
        Console.WriteLine($"  {window.AudioStatus}");
        Console.WriteLine(Line($"  připravených zvuků: {window.LoadedSounds}"));
        Console.WriteLine(Line($"  zkouška přehrání  : {window.AudioProbe}"));
        Console.WriteLine(Line($"  rozdíl culling on/off: {window.CullingDifference * 100.0:F2} %"));
        Console.WriteLine();
        Console.WriteLine("Rozpad času hlavního vlákna po fázích (tohle je vodítko pro ladění FPS):");
        Console.WriteLine(window.PhaseBreakdown());
        Console.WriteLine();
        Console.WriteLine("Nejhorší frame hlavního vlákna po částech:");
        Console.WriteLine(Line($"  nahrávání na GPU  : {window.WorstUploadMs:F2} ms"));
        Console.WriteLine(Line($"  plánování práce   : {window.WorstScheduleMs:F2} ms"));
        Console.WriteLine(Line($"  uvolňování chunků : {window.WorstUnloadMs:F2} ms"));
        Console.WriteLine(Line($"  vydávání kreslení : {window.WorstDrawMs:F2} ms"));
        Console.WriteLine();
        Console.WriteLine("Fronty streamingu na konci běhu:");
        Console.WriteLine($"  {window.StreamerQueues}");
        Console.WriteLine();
        Console.WriteLine("Úklid paměti během měřené části:");
        Console.WriteLine($"  {window.GcSummary}");
        Console.WriteLine(Line($"  nejdelší frame S úklidem gen2  : {window.WorstFrameWithGen2:F2} ms"));
        Console.WriteLine(Line($"  nejdelší frame BEZ úklidu gen2 : {window.WorstFrameWithoutGen2:F2} ms"));

        Console.WriteLine();
        Console.WriteLine(Line($"Rozsvícené pixely overlaye: {window.OverlayLitPixels}"));
        Console.WriteLine("První řádek overlaye přečtený zpět z framebufferu:");
        Console.WriteLine(window.OverlayProof ?? "(nezachyceno)");

        // Porovnává se počet odjetých framů, ne počet vzorků: kruhový buffer si drží
        // jen posledních 1024 framů, takže u delšího běhu by vzorků nikdy nebylo dost.
        bool framesOk = window.Timing.FrameCount >= requestedFrames - 1;
        bool driverOk = VulkanDebug.ErrorCount == 0;
        bool budgetOk = frame.P99Ms < FrameBudgetMs;
        bool overlayOk = window.OverlayLitPixels > 0;
        bool meshOk = window.RenderedChunks > 0 && window.TriangleCount > 0;

        // Dohled 16 chunků je kruh o poloměru 16, tedy zhruba 800 sloupců krát 4 patra.
        // Kdyby streaming zaostával, bude nahraných chunků řádově míň.
        bool streamingOk = window.LoadedChunks > 1000;
        // Porovnává se ustálený stav, ne studený start: časy při startu obsahují vrstvenou
        // kompilaci za běhu a o rychlosti meshovacího algoritmu nevypovídají.
        bool chunkBudgetOk = window.MeshSteadyStateWorstMs < 2.0;

        // Uzavřená geometrie se správným navíjením vypadá s cullingem i bez něj stejně;
        // odvrácené stěny jsou schované za přivrácenými. Obrácené navíjení by rozdíl vyhnalo nahoru.
        bool windingOk = window.CullingDifference is >= 0.0 and < 0.02;

        // Culling musí zahodit podstatnou část nahraných chunků. Přesnost ořezávacích rovin
        // hlídají jednotkové testy; tady jde o to, že je culling vůbec zapojený — kdyby
        // nebyl, rovnal by se počet viditelných počtu nahraných.
        //
        // Kritérium z F1 („po otočení kamery nesmí být vidět nic") tady už neplatí: svět
        // kameru obklopuje, takže i po otočení je pořád na co koukat.
        bool cullingOk = window.RenderedChunks > 0
            && window.VisibleChunks < window.RenderedChunks * 0.8;

        // Úprava bloků se musí projevit v geometrii. Kdyby se přemeshování nespustilo,
        // zůstal by počet trojúhelníků stejný a změna by na obrazovce nebyla vidět.
        bool editOk = window.TrianglesBeforeEdit > 0
            && window.TrianglesAfterEdit > 0
            && window.TrianglesBeforeEdit != window.TrianglesAfterEdit;

        // Tisíc stejně otesaných bloků musí sdílet jeden tvar. Kdyby se nesdílely,
        // byl by počet různých tvarů v řádu tisíců místo jednotek.
        // Musí platit obojí: tvary opravdu vznikly (jinak by nula prošla triviálně)
        // a je jich řádově míň než otesaných bloků.
        // Když nativní knihovna chybí, hra má běžet dál potichu — to je záměr, ne chyba.
        // Kritérium proto platí jen tehdy, když zvukový engine skutečně běží.
        bool audioOk = !window.AudioAvailable || window.AudioProbePassed;

        bool dedupOk = window.ChiselledBlocks >= 1000
            && window.UniqueMicroShapes >= 1
            && window.UniqueMicroShapes <= 4;

        Console.WriteLine();
        Console.WriteLine(Line($"[{(framesOk ? "OK  " : "CHYBA")}] doběhl požadovaný počet framů"));
        Console.WriteLine(Line($"[{(driverOk ? "OK  " : "CHYBA")}] zadna chyba ovladace"));
        Console.WriteLine(Line($"[{(budgetOk ? "OK  " : "CHYBA")}] p99 času framu pod {FrameBudgetMs:F2} ms"));
        Console.WriteLine(Line($"[{(overlayOk ? "OK  " : "CHYBA")}] overlay se opravdu vykreslil"));
        Console.WriteLine(Line($"[{(meshOk ? "OK  " : "CHYBA")}] svět se zameshoval a má geometrii"));
        Console.WriteLine(Line($"[{(streamingOk ? "OK  " : "CHYBA")}] streaming stihl nabrat dohled"));
        Console.WriteLine(Line($"[{(chunkBudgetOk ? "OK  " : "CHYBA")}] meshing chunku pod 2 ms v ustáleném stavu"));
        Console.WriteLine(Line($"[{(windingOk ? "OK  " : "CHYBA")}] stěny mají správné navíjení"));
        Console.WriteLine(Line($"[{(cullingOk ? "OK  " : "CHYBA")}] frustum culling odvrácené chunky zahodí"));
        Console.WriteLine(Line($"[{(editOk ? "OK  " : "CHYBA")}] úprava bloku se projevila v geometrii"));
        Console.WriteLine(Line($"[{(dedupOk ? "OK  " : "CHYBA")}] {window.ChiselledBlocks} otesaných bloků sdílí {window.UniqueMicroShapes} tvarů"));
        Console.WriteLine(Line($"[{(window.MicroChecksPassed ? "OK  " : "CHYBA")}] mikro paprsek i mikro kolize souhlasí"));
        Console.WriteLine(Line($"[{(audioOk ? "OK  " : "CHYBA")}] {(window.AudioAvailable ? "zvuk se opravdu přehrál" : "bez zvuku, hra běží dál (kritérium neplatí)")}"));

        bool passed = framesOk && driverOk && budgetOk && overlayOk && meshOk
                   && chunkBudgetOk && windingOk && cullingOk && streamingOk && editOk
                   && dedupOk && window.MicroChecksPassed && audioOk;
        Console.WriteLine(passed ? "SELFTEST PROŠEL" : "SELFTEST SELHAL");

        return passed ? 0 : 1;
    }

    private static string Line(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
