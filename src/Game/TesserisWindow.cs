using System.Diagnostics;
using System.Globalization;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using Tesseris.Engine.Audio;
using Tesseris.Engine.Core;
using Tesseris.Engine.MathLib;
using Tesseris.Engine.Rendering;
using Silk.NET.Vulkan;
using Tesseris.Engine.Rendering.Vulkan;
using GlfwVkHandle = OpenTK.Windowing.GraphicsLibraryFramework.VkHandle;
using Tesseris.Game.Blocks;
using ImGuiNET;
using Tesseris.Game.Colony;
using Tesseris.Game.Content;
using Tesseris.Game.Entities;
using Tesseris.Game.Items;
using Tesseris.Game.Lua;
using Tesseris.Game.Micro;
using Tesseris.Game.Modding;
using Tesseris.Game.Modding.Client;
using Tesseris.Game.Modding.Client.Rendering;
using Tesseris.Game.Player;
using Tesseris.Game.Power;
using Tesseris.Game.UI;
using Tesseris.Game.World;
using Tesseris.Game.World.Definitions;


namespace Tesseris.Game;

/// <summary>Okno hry: drží svět, streaming, hráče a ladicí overlay.</summary>
public sealed class TesserisWindow : GameWindow
{
    private const string DiscordUrl = "https://discord.gg/Mjmj8gmAPT";
    private const string PatreonUrl = "https://patreon.com/ArcflareG?utm_medium=unknown&utm_source=join_link&utm_campaign=creatorshare_creator&utm_content=copyLink";
    private const string CreateModUrl = "https://yoofecz.github.io/Tesseris-doc/content/";

    /// <summary>
    /// Kolik stupňů otočení připadá na jeden dílek pohybu myši.
    /// </summary>
    /// <remarks>
    /// <para><b>Bylo 0,12 a to je nehratelné.</b> Myš hlásí surové dílky, kterých je na
    /// palec tolik, kolik má DPI. Při běžných 1600 DPI vyjde celá otočka na
    /// <c>360 / 0,12 = 3000</c> dílků, tedy necelých <b>pět centimetrů</b> pohybu myší.
    /// Ve střílečkách se běžně jezdí na dvaceti až padesáti centimetrech na otočku.</para>
    ///
    /// <para>Nová výchozí hodnota dává 19 cm na otočku při 1600 DPI a 38 cm při 800 DPI,
    /// což je v obvyklém pásmu. Přesnou hodnotu si stejně každý nastaví sám — je v menu
    /// pod F4 jako „Citlivost mysi", zobrazená stokrát zvětšená, aby se dala číst.</para>
    /// </remarks>
    private float _mouseSensitivity = 0.03f;

    /// <summary>Jak daleko hráč dosáhne na bloky.</summary>
    private const float ReachDistance = 6f;

    /// <summary>Krok vzorkování obrazovky při kontrole orientace stěn.</summary>
    private const int SceneSampleStep = 8;

    /// <summary>
    /// Jak daleko je vidět pod hladinou. Voda pohlcuje světlo řádově rychleji než vzduch,
    /// takže i v čisté vodě je z dvaceti metrů jen modrá tma.
    /// </summary>
    /// <remarks>
    /// Zvednuto z 26 na 60: při 26 se pod vodou nedalo nic dělat, protože nebylo vidět
    /// ani dno pod sebou. Šedesát je pořád mnohem míň než na vzduchu, takže je poznat,
    /// že je hráč pod hladinou.
    /// </remarks>
    private const float UnderwaterViewDistance = 60f;

    /// <summary>Rychlost letu v selftestu, v blocích za sekundu.</summary>
    private const float SelftestFlightSpeed = 45f;

    /// <summary>Kolik bloků nad povrchem letí kamera při selftestu.</summary>
    private const float SelftestFlightClearance = 6f;

    private readonly Camera _camera = new(Vector3.Zero);
    private const float CameraStepSmoothing = 12f;
    private float _cameraStepOffset;

    /// <summary>
    /// Hodiny běhu. Jediné, co z nich zatím čte shader, je vlnění hladiny.
    /// </summary>
    /// <remarks>
    /// Schválně to nejsou sečtené delty snímků: ty se sčítáním nasbírají chybu a vlnění
    /// by se po delší hře rozešlo s reálným časem. Stopky měří přímo.
    /// </remarks>
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private byte[]? _flickerPrevious;
    private byte[]? _flickerWorst;
    private int _flickerRecording;
    private bool _f9WasDown;
    private bool _gWasDown;
    private bool _eWasDown;
    private bool _escWasDown;
    private double _flickerBrightness;
    private readonly List<(double Diff, bool Epoch)> _flickerSamples = [];
    private bool _flickerCapture;

    private readonly PlayerController _player = new(Vector3.Zero);
    private readonly GameModAccess _modGame = new();

    /// <summary>Životy, dech a zranění z pádu.</summary>
    private readonly Vitals _vitals = new();

    /// <summary>Kde hráč začínal. Sem se vrací po smrti.</summary>
    private Vector3 _spawn;
    private readonly FrameTimer _frameTimer = new();
    private readonly RenderStats _stats = new();
    private readonly Frustum _frustum = new();
    private readonly int _selftestFrames;
    private ulong _modTick;
    private EntityWorld? _modEntities;
    private AnimalPopulation? _animals;
    private LuantiLuaRuntime? _luaEntities;
    private ModWorldRuntime? _modWorldRuntime;
    private ModRuntimeSaveCoordinator? _modRuntimeSave;

    /// <summary>Obrazovka, ve které se běžný (ne-selftest) běh právě nachází.</summary>
    private StartupStage _startupStage;

    private enum StartupStage
    {
        MainMenu,
        PreparingWorld,
        Playing,
    }

    /// <summary>Kolik generovacích úloh tvořilo celý zvolený předgenerovaný okruh.</summary>
    private int _preparationTotal;

    /// <summary>Je už zařazený první snímek přípravy, ze kterého lze odečíst celkový počet?</summary>
    private bool _preparationStarted;

    private bool _startupLeftWasDown;
    private bool _startupBackspaceWasDown;
    private bool _startupTabWasDown;
    private bool _startupEnterWasDown;
    private bool _startupLeftKeyWasDown;
    private bool _startupRightKeyWasDown;
    private bool _startupEscapeWasDown;
    private readonly PlayerOptions _playerOptions;
    private bool _pauseMenuOpen;
    private bool _optionsMenuOpen;
    private bool _pauseLeftWasDown;
    private bool _restartToMainMenuAfterUnload;

    private MainMenu? _mainMenu;
    private ModManagerState? _modManager;
    private bool _modManagerOpen;
    private int _modManagerPage;
    private bool _graphicsSettingsOpen;
    private int _graphicsSettingsRow;
    private bool _graphicsRestartRequired;
    private string _graphicsDetectionSummary = "AUTO: waiting for GPU";
    // MainMenu zůstává po vstupu do světa živé právě kvůli jazyku vybranému hráčem.
    private StartupLanguage UiLanguage => _mainMenu?.Language ?? StartupLanguage.Czech;
    private WorldCatalog? _worldCatalog;
    private IReadOnlyList<WorldInfo> _knownWorlds = [];
    private int _continueWorldIndex;
    private WorldInfo? _continueWorld;
    private IReadOnlyList<Tesseris.ModApi.ModWorldPresetDefinition> _worldPresets = [];
    private int _worldPresetIndex;
    private string _startupError = string.Empty;
    private bool _modCommandOpen;
    private bool _ignoreNextCommandSlashText;
    private string _modCommandText = string.Empty;
    private readonly List<string> _modCommandMessages = [];
    private float _modCommandMessageSeconds;

    private const int PhaseStreaming = 0;
    private const int PhaseFarTerrain = 1;
    private const int PhaseDraw = 2;
    private const int PhaseOverlay = 3;
    private const int PhaseWaitGpu = 4;
    private const int PhasePresent = 5;
    private const int PhaseUpload = 6;
    private const int PhaseSchedule = 7;
    private const int PhaseFence = 8;
    private const int PhaseAcquire = 9;

    /// <summary>
    /// Rozpad času hlavního vlákna po fázích. Bez něj se dá u snímkové frekvence jen hádat:
    /// hlásilo se jen maximum přes celý běh, které o typickém framu neříká nic.
    /// </summary>
    private readonly PhaseProfiler _phases = new(
        1024,
        "streaming",
        "vzdaleny teren",
        "kresleni",
        "overlay",
        "cekani na GPU",
        "odeslani",
        "  z toho nahrani",
        "  z toho planovani",
        "  z toho fence (GPU)",
        "  z toho acquire");

    // ================= PEVNÝ SIMULAČNÍ TICK (T1) =================
    //
    // Do téhle chvíle jela celá hra z délky snímku, takže se chovala jinak na jiném stroji.
    // Teď je simulace oddělená: hodiny odměří pevné kroky 1/60 s, systémy se hýbou jen v nich
    // a render mezi dvěma stavy interpoluje.

    private const int TickPhasePathfinding = 0;
    private const int TickPhaseBelts = 1;
    private const int TickPhaseMachines = 2;
    private const int TickPhaseEntities = 3;
    private const int TickPhaseFluid = 4;
    private const int TickPhaseNature = 5;
    private const int TickPhaseOther = 6;

    /// <summary>Celý tik. Měří se zvlášť, ne součtem — p50 součtu není součet p50.</summary>
    private const int TickPhaseTotal = 7;

    private readonly FixedClock _simClock = new();

    // ================= KOLONIE (T2, T3, T5, T6) =================
    //
    // Všechno v jedné referenci schválně: TesserisWindow má přes deset tisíc řádků a audit
    // ho označil za hlavní překážku každého dalšího kroku. Kolonie se sem proto nevlévá
    // po kusech.
    /// <summary>
    /// Panely velitele. Immediate-mode knihovna — vlastní
    /// retained-mode UI je nejčastější místo, kde vlastní engine sežere rok.
    /// </summary>
    private ImGuiVulkanRenderer? _imgui;

    private readonly ColonyRuntime _colony = new();
    private readonly CommanderView _commander = new();
    private readonly AreaSelection _selection = new();
    private bool _tabWasDown;

    /// <summary>Co velitel právě staví. Nula znamená označování oblasti.</summary>
    private CommanderTool _tool = CommanderTool.Select;

    /// <summary>Kterým směrem se staví pás. Přepíná se klávesou R.</summary>
    private int _buildFacing;

    private bool _buildRotateWasDown;

    /// <summary>Nářadí velitele. Vybírá se číslicemi, protože ty jsou v prstech odjakživa.</summary>
    private enum CommanderTool
    {
        Select,
        Belt,
        Crusher,
        Inserter,

        /// <summary>Bourání. Druhá půlka stavění — bez něj je každá chyba trvalá.</summary>
        Demolish,
    }

    /// <summary>Čtyři světové strany pro stavbu pásu.</summary>
    private static readonly Vector3i[] BuildDirections =
    [
        new(1, 0, 0),
        new(0, 0, 1),
        new(-1, 0, 0),
        new(0, 0, -1),
    ];

    /// <summary>
    /// Rozpad času simulačního tiku po kategoriích. Kategorie jsou zadané dopředu podle toho,
    /// co bude v kolonii utrácet čas — pathfinding, pásy a stroje — i když dvě z nich zatím
    /// nic neměří. Prázdná kategorie stojí nula a je vidět, že je prázdná.
    /// </summary>
    private readonly PhaseProfiler _tickPhases = new(
        1024,
        "pathfinding",
        "pasy",
        "stroje",
        "entity",
        "kapaliny",
        "priroda",
        "ostatni",
        "celkem");

    /// <summary>Kolik tiků proběhlo v posledním snímku. Do overlaye.</summary>
    private int _ticksLastFrame;

    /// <summary>
    /// Co hráč chce dělat. Vzorkuje se každý snímek ze vstupu, uplatní se v pevném tiku.
    /// </summary>
    /// <remarks>
    /// Rozdělení je nutné: vstup se musí číst při každém snímku, jinak by se při 144 Hz
    /// zahodily dvě třetiny stisků, ale pohyb musí jít pevným krokem, jinak není deterministický.
    /// </remarks>
    private readonly record struct MovementIntent(
        Vector3 Wish,
        bool Jump,
        bool Sprint,
        float VerticalWish,
        bool Crouch);

    private MovementIntent _movementIntent;

    /// <summary>Nastaví se, když hráč stojí nad nenačteným chunkem — pak se fyzika neuplatní.</summary>
    private bool _movementBlocked;

    private readonly DebugMenu _menu = new();

    /// <summary>
    /// Panel obrazu a barev na F9.
    /// </summary>
    /// <remarks>
    /// Schválně druhé menu, ne další oddíl v tom vývojářském. To má přes padesát řádků
    /// meshingu, front a naměřených časů — barvy by se v něm hledaly. Tenhle má jen to,
    /// co je vidět na obraze, a vejde se celý na obrazovku bez posouvání.
    /// </remarks>
    private readonly DebugMenu _lookMenu = new();
    private readonly FogTuning _fogTuning = new();
    private bool _f4WasDown;
    private bool _f10WasDown;
    private bool _menuResetWasDown;
    private double _diagnosticsAge = 1.0;
    private bool _menuUpWasDown;
    private bool _menuDownWasDown;
    private bool _menuLeftWasDown;
    private bool _menuRightWasDown;

    private int _voicesBeforeProbe;
    private Vector3i _probeSoundBlock;

    /// <summary>
    /// Čekat na obnovu obrazu? Ve hře zapnuto, v selftestu vypnuto.
    ///
    /// <para>Není to <c>readonly</c>, aby to šlo přepnout z dev menu. Bez toho není vidět,
    /// kolik má engine rezervy: se zapnutým vsyncem se snímek vyrobí za necelou milisekundu
    /// a zbylých pět se čeká na monitor, takže FPS ukazuje jeho frekvenci, ne výkon hry.</para>
    /// </summary>
    private bool _vsync;

    private BlockRegistry? _registry;
    private ModHost? _mods;
    private ExternalModLoaderDiscovery? _externalModLoaders;
    private ContentCatalog? _contentCatalog;
    private ContentAssetResolver? _contentAssets;
    private ModWorldGenerationPipeline? _modWorldGeneration;
    private VoxelWorld? _world;

    private readonly Dictionary<Vector3i, (long Time, float Light)> _dropLightCache = [];
    private readonly Queue<(Vector3i Position, byte Distance)> _dropLightQueue = new();
    private readonly HashSet<Vector3i> _dropLightVisited = [];
    private readonly Dictionary<Vector3i, (long Time, float Light)> _skyLightCache = [];
    private readonly Queue<(Vector3i Position, byte Distance)> _skyLightQueue = new();
    private readonly HashSet<Vector3i> _skyLightVisited = [];
    private TerrainGenerator? _generator;
    private JobSystem? _jobs;
    private ChunkStreamer? _streamer;
    private FluidSimulation? _fluid;
    private WorldStorage? _storage;
    private FarTerrain? _farTerrain;
    private TextureArray? _textures;
    private ChunkRenderer? _chunkRenderer;
    private TextRenderer? _text;
    private OutlineRenderer? _outline;
    private Hotbar? _hotbar;
    private ItemRegistry? _items;
    private Inventory? _inventory;
    private Mining? _mining;
    private ItemEntities? _drops;

    /// <summary>Kreativní režim: nekope se, nic se neopotřebovává a materiál nedochází.</summary>
    private bool _creative;
    private int _crackLayer = -1;

    /// <summary>Vrstva textury s rukou. Kreslí se, když hráč nic nedrží.</summary>
    private int _handLayer = -1;
    private int _playerSkinLayer = -1;

    /// <summary>Pohled na hráče. Cyklí se klávesou F5, stejně jako to má Luanti.</summary>
    private enum CameraView { First, ThirdBack, ThirdFront }

    private CameraView _cameraView = CameraView.First;
    private float _playerWalkPhase;
    private float _playerAnimationTime;
    private bool _f5WasDown;

    /// <summary>
    /// Rozmach ruky, 0 až 1. Nula znamená klid.
    /// </summary>
    /// <remarks>
    /// Doběhne vždycky celý, i když hráč tlačítko pustí hned. Useknutý rozmach v půlce
    /// vypadá jako záškub, ne jako úder.
    /// </remarks>
    private float _swing;
    private SpriteRenderer? _sprites;
    private InventoryScreen? _inventoryScreen;
    private RecipeBook? _recipes;
    private TreeFelling? _felling;
    private SaplingGrowth? _saplings;
    private LivingVegetation? _ecology;
    private bool _ecologyLoaded;
    private Furnaces? _furnaces;
    private Chests? _chests;
    private PowerNetwork? _power;
    private readonly MeshBuffer _powerMesh = new();
    private int _powerMeshRevision = -1;
    private readonly MeshBuffer _animatedBuildingPartMesh = new();
    private readonly Dictionary<Vector3i, ActiveBuildingPartAnimation> _buildingPartAnimations = [];
    private readonly List<BuildingPartAnimation> _buildingPartAnimationScratch = [];
    private int[] _powerCableLayers = [];
    private int _overheadWireLayer = -1;
    private int _junctionBoxLayer = -1;
    private Vector3i? _pendingWireEndpoint;
    private string _powerNotice = string.Empty;
    private float _powerNoticeSeconds;
    private Vector3i _openFurnace;
    private Vector3i _openChest;
    private Vector3i? _openChestPartner;
    private Vector3i? _structureCornerOne;
    private Vector3i? _structureCornerTwo;
    private AudioEngine? _audio;
    private BlockSounds? _sounds;
    private SoundHandle _animaliaSheepHurt;
    private SoundHandle _animaliaSheepDeath;
    private SoundHandle _animaliaReindeerHurt;
    private SoundHandle _animaliaReindeerDeath;
    private GameModAudioBridge? _modAudioBridge;
    private GameModRenderBridge? _modRenderBridge;

    private VulkanContext? _vulkan;
    private VulkanSwapchain? _swapchain;
    private VulkanRenderer? _renderer;

    /// <summary>
    /// Snímek, ve kterém se má přečíst obrazovka. Ve Vulkanu se nedá číst kdykoli jako
    /// přes <c>glReadPixels</c> — kopie se musí nahrát do příkazového bufferu ještě před
    /// odesláním snímku, takže se o ni musí požádat dopředu.
    /// </summary>
    private bool _captureThisFrame;

    private readonly ChiselTool _chisel = new();

    /// <summary>Kolik metrů chůze dělí dva kroky.</summary>
    private const float StepDistance = 2.2f;

    private float _walkedSinceStep;
    private Vector3 _previousFootPosition;

    /// <summary>Poloha očí na začátku posledního tiku. Bez ní není mezi čím interpolovat.</summary>
    private Vector3 _previousEyePosition;

    /// <summary>Má už <see cref="_previousEyePosition"/> platnou hodnotu?</summary>
    private bool _hasPreviousEye;

    /// <summary>Poloha nohou na začátku posledního tiku. Pro model postavy a fázi chůze.</summary>
    private Vector3 _previousTickFoot;

    /// <summary>Interpolovaná poloha nohou z minulého SNÍMKU. Jen pro fázi chůze.</summary>
    private Vector3 _previousDrawFoot;

    private bool _overlayVisible = true;
    private bool _chiselMode;
    private bool _f3WasDown;
    private bool _fWasDown;
    private bool _vWasDown;
    private bool _rWasDown;
    private bool _tWasDown;
    private bool _plusWasDown;
    private bool _minusWasDown;
    private bool _slashWasDown;
    private bool _multiplyWasDown;
    private bool _leftWasDown;
    private bool _rightWasDown;
    private bool _modScreenLeftWasDown;
    private bool _modScreenRightWasDown;
    private Vector2 _modScreenPointer;
    private bool _ignoreNextMouseDelta = true;

    private readonly HashSet<ushort> _modLifecycleBlocks = [];
    private readonly HashSet<ushort> _modIntervalBlocks = [];

    /// <summary>Poloha myši z minulého snímku. Rozdíl proti ní je posun pohledu.</summary>
    private Vector2 _lastMousePosition;

    private bool[]? _sceneWithCulling;
    private bool[]? _sceneWithoutCulling;

    private int[] _gcAtStart = [0, 0, 0];
    private long _allocatedAtStart;
    private int _lastGen2Count;

    public TesserisWindow(
        GameWindowSettings gameSettings, NativeWindowSettings nativeSettings, int selftestFrames, bool vsync)
        : base(gameSettings, nativeSettings)
    {
        _selftestFrames = selftestFrames;
        _playerOptions = PlayerOptions.Load();
        _vsync = selftestFrames > 0 ? vsync : _playerOptions.VSync;
        // Vsync se tu NENASTAVUJE. Swapchain vznikne až v InitializeRendering s hodnotou
        // _vsync, takže by to bylo jen zbytečné přestavění hned po startu — a to se pere
        // s pipeline, které se mezitím postavily podle původního swapchainu.
        _mouseSensitivity = _playerOptions.MouseSensitivity;
        _camera.FieldOfViewDegrees = _playerOptions.FieldOfView;
    }

    public FrameTimer Timing => _frameTimer;

    /// <summary>
    /// Ohlásil ovladač chybu? Selftest podle toho pozná, že snímek sice vznikl, ale kreslení
    /// se nepovedlo.
    /// </summary>
    public static bool SawDriverError => VulkanDebug.ErrorCount > 0;

    /// <summary>
    /// Kolik bufferů drží geometrie.
    /// </summary>
    /// <remarks>
    /// Ve Vulkanu tu byl i strop, protože počet alokací paměti zařízení je tam tvrdě
    /// omezený. OpenGL takový limit nemá — buffery spravuje ovladač sám — takže zbylo
    /// jen to, kolik jich je.
    /// </remarks>
    public string BufferAllocations
    {
        get
        {
            (int live, int peak) = VulkanBuffer.AllocationCount;

            return string.Create(
                CultureInfo.InvariantCulture,
                $"{live} živých, nejvíc {peak} naráz");
        }
    }

    /// <summary>Zařízení, na kterém se kreslí. Odpovídá <c>GL_RENDERER</c>.</summary>
    public string GpuName => _renderer is null ? "(nespuštěno)" : (_vulkan is null ? "(nespusteno)" : $"{_vulkan.DeviceName}, Vulkan {_vulkan.ApiVersion}");

    /// <summary>
    /// Rozlišení, do kterého se opravdu kreslí, v pixelech.
    ///
    /// Do reportu patří proto, že bez něj se čas framu nedá vyhodnotit. Na displeji
    /// s vyšším rozlišením (Retina) je framebuffer dvakrát větší v každém rozměru než
    /// okno, tedy <b>čtyřikrát tolik pixelů</b> — a rozpočet 16,67 ms znamená při každém
    /// rozlišení něco jiného.
    /// </summary>
    public string RenderResolution => _renderer is null
        ? "(nespuštěno)"
        : string.Create(
            CultureInfo.InvariantCulture,
            $"{(int)_swapchain!.Extent.Width}x{(int)_swapchain!.Extent.Height} px"
            + $" (okno {ClientSize.X}x{ClientSize.Y} bodů, měřítko {(int)_swapchain!.Extent.Width / (float)Math.Max(ClientSize.X, 1):F1}x)");

    public int OverlayLitPixels { get; private set; }

    public string? OverlayProof { get; private set; }

    public double MeshSteadyStateWorstMs { get; private set; } = double.NaN;

    public int WorkerCount => _jobs?.WorkerCount ?? 0;

    public int ViewDistanceChunks => _streamer?.ViewDistanceChunks ?? 0;

    public int LoadedChunks { get; private set; }

    public int RenderedChunks { get; private set; }

    public int VisibleChunks => _chunkRenderer?.VisibleChunks ?? 0;

    public int TriangleCount { get; private set; }

    public float FlightDistance { get; private set; }

    public string RaycastResult { get; private set; } = "(nespuštěno)";

    public int VisibleChunksLookingAway { get; private set; } = -1;

    public double CullingDifference { get; private set; } = double.NaN;

    public double WorstUploadMs => _streamer?.WorstUploadMs ?? 0.0;

    public double WorstScheduleMs => _streamer?.WorstScheduleMs ?? 0.0;

    public double WorstUnloadMs => _streamer?.WorstUnloadMs ?? 0.0;

    public double WorstDrawMs { get; private set; }

    public string StreamerQueues { get; private set; } = "(nezměřeno)";

    public string GcSummary { get; private set; } = "(nezměřeno)";

    public double WorstFrameWithGen2 { get; private set; }

    public double WorstFrameWithoutGen2 { get; private set; }

    /// <summary>Kolik trojúhelníků měla scéna před zkušební úpravou bloků v selftestu.</summary>
    public int TrianglesBeforeEdit { get; private set; } = -1;

    /// <summary>Kolik jich měla poté, co se úprava promítla do geometrie.</summary>
    public int TrianglesAfterEdit { get; private set; } = -1;

    /// <summary>Slovní popis zkušební úpravy provedené v selftestu.</summary>
    public string EditProbe { get; private set; } = "(nespuštěno)";

    /// <summary>Výsledek zkoušky tesání: kolik bloků, kolik různých tvarů, jestli drží kolize.</summary>
    public string ChiselProbe { get; private set; } = "(nespuštěno)";

    /// <summary>Kolik otesaných bloků zkouška vytvořila.</summary>
    public int ChiselledBlocks { get; private set; }

    /// <summary>Kolik různých tvarů z toho vzniklo. Deduplikace se pozná právě odsud.</summary>
    public int UniqueMicroShapes { get; private set; }

    /// <summary>Prošly kontroly mikro kolizí a dvouúrovňového paprsku?</summary>
    public bool MicroChecksPassed { get; private set; }

    /// <summary>Stav zvuku. Bez nativní knihovny sem přijde vysvětlení proč.</summary>
    public string AudioStatus => _audio?.Status ?? "(nespuštěno)";

    /// <summary>Kolik zvuků se podařilo připravit.</summary>
    public int LoadedSounds => _sounds?.LoadedSounds ?? 0;

    /// <summary>Běží zvukový engine? Bez nativní knihovny je false a hra hraje potichu.</summary>
    public bool AudioAvailable => _audio?.IsAvailable ?? false;

    /// <summary>
    /// Výsledek zvukové zkoušky. Odečítá se během běhu, ne po něm — po
    /// <see cref="OnUnload"/> je zvukový engine zavřený a ptát se ho už nejde.
    /// </summary>
    public string AudioProbe { get; private set; } = "(neproběhla)";

    /// <summary>Zahrál poziční zvuk skutečně hlas navíc?</summary>
    public bool AudioProbePassed { get; private set; }

    protected override void OnLoad()
    {
        base.OnLoad();

        InitializeRendering();

        string runtimeRoot = Environment.GetEnvironmentVariable("TESSERIS_RUNTIME_ROOT")
            ?? AppContext.BaseDirectory;
        string assetsRoot = Path.Combine(runtimeRoot, "assets");
        string modsRoot = ResolveExtensionRoot("TESSERIS_MODS", "VOXELITY_MODS", "mods");
        string loadersRoot = ResolveExtensionRoot("TESSERIS_MODLOADERS", "VOXELITY_MODLOADERS", "modloaders");

        _modManager = ModManagerState.Load(modsRoot);

        _externalModLoaders = ExternalModLoaderDiscovery.Discover(loadersRoot);

        _mods = ModHost.DiscoverAndLoadStandalone(
            modsRoot,
            new ModHostOptions
            {
                Logger = GameModLogger.Instance,
                Loaders = _externalModLoaders.Loaders,
                Game = _modGame,
                GameVersion = "1.0.0",
                EnabledModIds = _modManager.EnabledModIds,
            });
        _mods.Initialize();
        _luaEntities = LuantiLuaRuntime.Load(assetsRoot, modsRoot);

        var vanillaContent = new ContentSource(
            "tesseris",
            assetsRoot,
            LoadOrder: 0,
            LegacyFlat: true);
        var contentSources = new List<ContentSource> { vanillaContent };
        int contentLoadOrder = 1;
        foreach (Tesseris.ModApi.ModContentSource source in _mods.ContentSources)
        {
            contentSources.Add(new ContentSource(
                source.ModId,
                source.RootPath,
                LoadOrder: contentLoadOrder++));
        }

        _contentCatalog = new ContentCatalog(contentSources);
        _contentAssets = new ContentAssetResolver(_contentCatalog, vanillaContent);
        _registry = BlockRegistry.Load(_contentCatalog);
        VanillaWorldPresetFactory.RegisterDefaults(_mods.WorldDefinitions, _registry);
        _mods.Freeze();
        _worldPresets = _mods.WorldDefinitions.Presets;
        Tesseris.ModApi.ResourceId configuredPreset = ConfiguredWorldPreset();
        int configuredPresetIndex = _worldPresets
            .Select((preset, index) => (preset, index))
            .Where(value => value.preset.Id == configuredPreset)
            .Select(value => value.index)
            .DefaultIfEmpty(0)
            .First();
        _worldPresetIndex = configuredPresetIndex;
        _mods.Screens.ActiveScreenChanged += OnModScreenChanged;
        _mods.Client.Ui.ActiveScreenChanged += OnModScreenV2Changed;
        _modGame.BlockChanged += OnModGameBlockChanged;
        ResolveModBehaviorTargets();
        _modWorldGeneration = new ModWorldGenerationPipeline(_registry, _mods.WorldGeneration);
        Log.Info($"Načteno {_registry.Count - 1} typů bloků, {_registry.TextureNames.Count} vrstev textur.");
        Log.Info(
            $"Načteno {_mods.LoadedMods.Count} modů a {_externalModLoaders.Loaders.Count} externích loaderů.");

        _jobs = new JobSystem();
        _hotbar = new Hotbar(_registry);

        // PŘEDMĚTY. Bloky se na ně převedou samy, ruční definice jsou jen pro nástroje
        // a suroviny — viz ItemRegistry.
        _items = ItemRegistry.Create(_registry, _contentCatalog);
        _inventory = new Inventory(_items);
        _mining = new Mining(_registry, _items);
        _drops = new ItemEntities(_items);
        _modGame.AttachStaticContent(_registry, _items, _inventory, _player, _vitals);

        // Ikony nástrojů a surovin do TÉHOŽ atlasu jako bloky. Druhý atlas by znamenal
        // druhou sadu vazeb kvůli hrstce obrázků.
        List<string> layers = [.. _registry.TextureNames];
        foreach (string icon in _items.IconTextures())
        {
            if (!layers.Contains(icon))
            {
                layers.Add(icon);
            }
        }

        // PRASKLINY. Deset stupňů za sebou, takže shaderu stačí index prvního a přičte
        // se k němu stupeň.
        string[] cableMaterials = ["cable_red", "cable_blue", "cable_yellow", "cable_green"];
        _powerCableLayers = new int[cableMaterials.Length];
        for (int i = 0; i < cableMaterials.Length; i++)
        {
            _powerCableLayers[i] = layers.Count;
            layers.Add(cableMaterials[i]);
        }

        _overheadWireLayer = layers.Count;
        layers.Add("overhead_wire");
        _junctionBoxLayer = layers.Count;
        layers.Add("cable_junction");

        // CO POSTAVILA KOLONIE: pás, stroj, vkládač a item na pásu. Jména drží
        // ColonyTextureLayers, protože jediný seznam je jediná pravda — kdyby si je okno
        // psalo znovu, rozejde se to při první změně.
        //
        // PŘIHLÁSÍ SE JEN TO, CO OPRAVDU EXISTUJE. Atlas na neznámé jméno nespadne, vyrobí
        // náhradní dlaždici — takže by se vrstva „našla“ a kontrola chybějící vrstvy by nikdy
        // nic nenašla. Ověřeno sondou: s vymyšleným jménem vyšla vrstva 212 a hlášení mlčelo.
        // Existenci proto musí ověřit tenhle kód, ne až IndexOf.
        foreach (string name in ColonyTextureLayers.Names)
        {
            if (layers.Contains(name))
            {
                continue;
            }

            string? texturePath = _contentAssets.ResolveTexturePath(name);
            if (texturePath is null || !File.Exists(texturePath))
            {
                Log.Warn($"Textura '{name}' pro kolonii neexistuje; vrstva zustane neprihlasena.");
                continue;
            }

            layers.Add(name);
        }

        _crackLayer = layers.Count;
        for (int stage = 0; stage < 10; stage++)
        {
            layers.Add($"crack_{stage}");
        }

        // RUKA. Není to předmět, takže ji do atlasu nikdo jiný nepřihlásí — a přitom se
        // ve světě staví z obrysu úplně stejně jako meč nebo klacek.
        _handLayer = layers.Count;
        // Verze je součást názvu schválně. Starší buildy si procedurální dlaždici `hand`
        // uložily vedle binárky a loader by tu průhlednou cache dál upřednostňoval před
        // opraveným generátorem. Nová vrstva dostane čistou, autoritativní UV kresbu.
        layers.Add("player_arm_v2");

        // KŮŽE POSTAVY. Není čtvercová dlaždice bloku, ale UV mapa (64×32), proto se atlasu
        // hlásí mezi skiny — jinak by ji zahodil kvůli poměru stran.
        _playerSkinLayer = layers.Count;
        layers.Add("character");

        // IKONY ROZHRANÍ: kniha receptů a obrysy do prázdných slotů výstroje.
        int bookLayer = layers.Count;
        layers.Add("ui_book");

        int playerLayer = layers.Count;
        layers.Add("ui_player");

        int[] armourHints = new int[Inventory.ArmourSlots];

        foreach ((int index, string part) in new[] { (0, "head"), (1, "chest"), (2, "legs"), (3, "feet") })
        {
            armourHints[index] = layers.Count;
            layers.Add($"ui_slot_{part}");
        }

        // RUČNÍ MODELY z Blockbenche. Musí se načíst PŘED atlasem, protože si o své vrstvy
        // teprve řeknou — jejich textura je vysoká a dělí se na několik dlaždic.
        List<ItemModelFile> models = ItemModelFile.LoadAll(
            _contentCatalog, _contentAssets, ItemModelHalf);

        foreach (ItemModelFile model in models)
        {
            foreach (string name in model.LayerNames())
            {
                if (!layers.Contains(name))
                {
                    layers.Add(name);
                }
            }
        }

        MobVisualRegistry mobVisuals = MobVisualRegistry.ReadManifests(
            Path.Combine(assetsRoot, "mobs", "mobs-redo-test-only.json"),
            Path.Combine(assetsRoot, "mobs", "animalia.json"));
        foreach (string texture in mobVisuals.TextureNames)
        {
            if (!layers.Contains(texture))
                layers.Add(texture);
        }

        int sheepSkinLayer = layers.IndexOf("mobs_sheep_white_test_nc");
        int deerSkinLayer = layers.IndexOf("animalia_reindeer");
        int wolfSkinLayer = layers.IndexOf("animalia_wolf_1");
        int wolfSkin2Layer = layers.IndexOf("animalia_wolf_2");
        int wolfSkin3Layer = layers.IndexOf("animalia_wolf_3");
        int wolfSkin4Layer = layers.IndexOf("animalia_wolf_4");

        _items.ResolveIcons(name => layers.IndexOf(name));

        // VRSTVY KOLONIE PODLE JMÉNA. IndexOf vrátí na neznámé jméno −1 a Resolve tu −1
        // nechá být; nula by se od platné vrstvy nedala odlišit a přesně tak se sem
        // počtvrté dostala cizí textura.
        _colonyLayers = ColonyTextureLayers.Resolve(name => layers.IndexOf(name));
        if (!_colonyLayers.IsComplete)
        {
            // Chybějící vrstva se nedá uhádnout, ale dá se ohlásit. Bez tohohle by se na ni
            // přišlo až očima ve hře — a to je ta cesta, po které se sem ta chyba pokaždé
            // vrátila.
            Log.Warn(
                "Kolonii chybi vrstvy textur: "
                + string.Join(", ", _colonyLayers.MissingNames())
                + ". Co je bez vrstvy, se nekresli.");
        }

        // Skiny zvířat jsou UV mapy, ne dlaždice bloků — atlas je nesmí zahodit kvůli
        // nečtvercovému rozměru. Bez toho vypadne 45 ze 105 skinů z Mobs Redo.
        _textures = new TextureArray(
            _vulkan!,
            layers, _contentAssets.ResolveTexturePath,
            mobVisuals.TextureNames.Append("character").ToHashSet(StringComparer.Ordinal));

        // Kreslení ikon. Bere týž atlas jako bloky, takže musí vzniknout až po něm.
        _sprites = new SpriteRenderer(_vulkan!, _renderer!, _swapchain!, _textures);
        _inventoryScreen = new InventoryScreen(_items);
        _inventoryScreen.SetIcons(bookLayer, playerLayer, armourHints);
        _recipes = RecipeBook.Load(_items, _contentCatalog);
        _felling = new TreeFelling(_registry, _items);
        _furnaces = new Furnaces(_items, _recipes);
        _chests = new Chests(_items);
        _chunkRenderer = new ChunkRenderer(
            _vulkan!, _renderer!, _swapchain!, _textures, _playerOptions.MapDetailSize);
        _chunkRenderer.AnimalTextures = new AnimalTextureLayers(
            sheepSkinLayer,
            deerSkinLayer,
            wolfSkinLayer,
            wolfSkin2Layer,
            wolfSkin3Layer,
            wolfSkin4Layer);
        mobVisuals.LoadModels(
            assetsRoot, texture => layers.IndexOf(texture), layer => _textures!.SkinScale(layer));

        // Kontrola pokrytí: druh bez naimportovaného modelu spadne na náhradní kvádr a ve světě
        // vypadá jako cizí těleso. Radši ať je to vidět v logu, než aby se na to přišlo očima.
        string[] withoutVisual = MobDefinitions.All
            .Where(definition => !mobVisuals.HasVisual(definition.Id)
                && !mobVisuals.HasVisual(definition.SourceId))
            .Select(definition => definition.Id)
            .ToArray();
        Log.Info(withoutVisual.Length == 0
            ? $"Mobů: {MobDefinitions.All.Count}, všichni mají naimportovaný model."
            : $"Mobů: {MobDefinitions.All.Count}, BEZ MODELU {withoutVisual.Length}: {string.Join(", ", withoutVisual)}");
        _chunkRenderer.MobVisuals = mobVisuals;

        // POSTAVA HRÁČE: Minetest Sam z Luanti. Model je 17 jednotek vysoký, takže se zmenší
        // na výšku hráče. Nohy modelu začínají v nule, žádné dorovnání proto nepotřebuje.
        _chunkRenderer.PlayerVisual = B3dAnimatedModel.Load(
            Path.Combine(assetsRoot, "models", "character.b3d"),
            B3dAnimationProfile.Character,
            PlayerController.Height / 17f);
        _chunkRenderer.PlayerSkinUvScale = _textures.SkinScale(_playerSkinLayer);

        // VRSTVA KŮŽE PATŘÍ K MODELU, NE KE KAMEŘE. Nastavovala se na konci ApplyCameraView,
        // jenže ta se v první osobě vrátí dřív — takže vrstva zůstala na výchozí nule.
        // Nula je v abecedně tříděném atlasu acacia_leaves, tedy listí, a kolonisté chodili
        // po světě zelení. U hráče to nevadilo jen proto, že se v první osobě nekreslí.
        //
        // Je to potřetí, co se index vrstvy rozešel s tím, co má představovat. Pokaždé kvůli
        // číslu, které se někde nenastavilo nebo se nastavilo natvrdo.
        _chunkRenderer.PlayerSkinLayer = _playerSkinLayer;


        // RUKA V PRVNÍ OSOBĚ JE TATÁŽ PAŽE JAKO U POSTAVY. Pruh paže Minetest Sama leží
        // na U 40–56, V 16–32 textury 64×32; přepočte se do dlaždice atlasu i s tím, jakou
        // její část skin zabírá.
        Vector2 skin = _chunkRenderer.PlayerSkinUvScale;
        _chunkRenderer.HandUvMin = new Vector2(40f / 64f * skin.X, 16f / 32f * skin.Y);
        _chunkRenderer.HandUvSize = new Vector2(16f / 64f * skin.X, 16f / 32f * skin.Y);
        _chunkRenderer.Fxaa = _playerOptions.Fxaa;
        _chunkRenderer.FancyWater = _playerOptions.FancyWater;
        _chunkRenderer.Day.NightBrightness = _playerOptions.NightBrightness;
        _chunkRenderer.Day.NightBrightnessCeiling = _playerOptions.NightBrightnessCeiling;
        ApplyLookOptions();

        ApplyGraphicsQuality(updateViewDistance: false);

        BindItemModels(models, layers);
        _text = new TextRenderer(_vulkan!, _renderer!, _swapchain!);
        _imgui = new ImGuiVulkanRenderer(_vulkan!, _renderer!, _swapchain!, typeof(TesserisWindow).Assembly);
        _outline = new OutlineRenderer(_vulkan!, _renderer!, _swapchain!);
        _modRenderBridge = new GameModRenderBridge(
            _mods.Client,
            _contentCatalog,
            _contentAssets,
            _vulkan!,
            _renderer!,
            _swapchain!);

        _audio = new AudioEngine();
        _audio.SetGlobalVolume(_playerOptions.MasterVolume);
        _sounds = new BlockSounds(_audio);
        _animaliaSheepHurt = _audio.CreateSoundFromFile(
            Path.Combine(assetsRoot, "audio", "mobs", "animalia_sheep_hurt.ogg"),
            minDistance: 1f,
            maxDistance: 20f);
        _animaliaSheepDeath = _audio.CreateSoundFromFile(
            Path.Combine(assetsRoot, "audio", "mobs", "animalia_sheep_death.ogg"),
            minDistance: 1f,
            maxDistance: 24f);
        _animaliaReindeerHurt = _audio.CreateSoundFromFile(
            Path.Combine(assetsRoot, "audio", "mobs", "animalia_reindeer_hurt.ogg"),
            minDistance: 1f,
            maxDistance: 20f);
        _animaliaReindeerDeath = _audio.CreateSoundFromFile(
            Path.Combine(assetsRoot, "audio", "mobs", "animalia_reindeer_death.ogg"),
            minDistance: 1f,
            maxDistance: 24f);
        _modAudioBridge = new GameModAudioBridge(_mods.Client.Audio, _contentAssets, _audio);
        _audio.PlayGlobal(_sounds.Ambient, volume: 0.25f);

        if (_selftestFrames > 0)
        {
            // Selftest obchází menu a používá historický seed. Ukládat normálně nemá co —
            // jenže ověřit save/load kolonie jinak než dvěma běhy nad stejnou složkou nejde,
            // a zelený test podle skillu není důkaz. Proměnná to na ten jeden účel zapne.
            InitializeWorld(
                seed: 20260727,
                worldDirectory: Environment.GetEnvironmentVariable("VOXELITY_SELFTEST_WORLD"));
            EnterGameplay(captureMouse: false);

            // V selftestu se prolétá svět, ne chodí — měří se streaming.
            _player.NoClip = true;

            MeasureSteadyStateMeshing();

            // Měření času grafiky po průchodech. V selftestu vždy — jinak není z čeho poznat,
            // který průchod stojí kolik.
            _chunkRenderer!.GpuProfiler.Enabled = true;

            _gcAtStart = [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];
            _allocatedAtStart = GC.GetTotalAllocatedBytes(precise: false);
        }
        else
        {
            OpenStartupMenu();
        }
    }

    /// <summary>
    /// Distribuovaná hra čte rozšíření vedle svého exe. Při vývoji přes `dotnet run` z
    /// kořene repozitáře čte přímo kořenové `mods`, takže sestavení modu nevyžaduje nový
    /// build hry. Proměnná prostředí má vždy nejvyšší prioritu.
    /// </summary>
    private static string ResolveExtensionRoot(
        string environmentVariable,
        string legacyEnvironmentVariable,
        string directoryName)
    {
        string? configured = Environment.GetEnvironmentVariable(environmentVariable)
            ?? Environment.GetEnvironmentVariable(legacyEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(configured);
        }

        string workingDirectory = Path.GetFullPath(Environment.CurrentDirectory);
        if (File.Exists(Path.Combine(workingDirectory, "Tesseris.sln"))
            || File.Exists(Path.Combine(workingDirectory, "Voxelity.sln")))
        {
            return Path.Combine(workingDirectory, directoryName);
        }

        return Path.Combine(AppContext.BaseDirectory, directoryName);
    }

    /// <summary>
    /// Postaví běhový svět až poté, co hráč v menu vybere save a seed.
    /// Grafika a atlas už v tu chvíli existují, takže se znovu nenačítají.
    /// </summary>
    private void InitializeWorld(
        int seed,
        string? worldDirectory,
        Tesseris.ModApi.ResourceId? selectedPreset = null)
    {
        if (_registry is null || _jobs is null || _chunkRenderer is null)
        {
            throw new InvalidOperationException("Statická část hry ještě není inicializovaná.");
        }

        _worldDirectory = worldDirectory;
        _world = new VoxelWorld(_registry);
        Tesseris.ModApi.ResourceId presetId = selectedPreset ?? SelectedWorldPreset();
        _modWorldRuntime = ModWorldRuntime.Create(_mods!.WorldDefinitions, seed, presetId);
        bool customWorld = _modWorldRuntime.Preset.Id != VanillaWorldPresetFactory.PresetId
            || _modWorldRuntime.Dimension.Id != VanillaWorldPresetFactory.OverworldId;
        _generator = new TerrainGenerator(
            _registry,
            seed,
            _modWorldGeneration,
            customWorld ? _modWorldRuntime : null);
        if (!customWorld)
        {
            _generator.EnableTrees(_registry);
        }
        _animals = new AnimalPopulation(_registry, _generator, seed, _luaEntities);

        // Sazeč se bere z generátoru, ne staví znovu: vyrostlý strom musí mít týž tvar
        // i týž seed jako ten vygenerovaný, jinak by v lese byly dva druhy dubů.
        _saplings = _generator.Trees is null ? null : new SaplingGrowth(_generator.Trees, _generator);
        _ecology = worldDirectory is null || _generator.Trees is null
            ? null
            : new LivingVegetation(_registry, _generator, _generator.Trees, seed);
        _ecologyLoaded = false;
        _power = new PowerNetwork();
        _powerMeshRevision = -1;
        _pendingWireEndpoint = null;
        _buildingPartAnimations.Clear();
        _animatedBuildingPartMesh.Clear();
        _chunkRenderer.AnimatedBuildingParts = _animatedBuildingPartMesh;

        _streamer = new ChunkStreamer(_world, _generator, _jobs, _chunkRenderer);
        _modGame.AttachWorld(_world, _streamer);
        ulong savedModTick = 0;
        if (worldDirectory is not null)
        {
            _modRuntimeSave = new ModRuntimeSaveCoordinator(worldDirectory);
            ModRuntimeLoadResult runtime = _modRuntimeSave.Load(
                _mods!.Entities,
                () => _mods.ItemStacks,
                () => _mods.Containers);
            if (runtime.HasRuntimeState)
            {
                _modEntities = runtime.Entities;
                savedModTick = runtime.SimulationTick;
            }
        }

        _modEntities ??= new EntityWorld(_mods!.Entities, seed);
        _mods.OpenWorld(worldDirectory, presetId, seed, savedModTick);
        _modTick = savedModTick;
        _streamer.ChunkLoaded += OnModChunkLoaded;
        _streamer.ChunkUnloading += OnModChunkUnloading;
        _streamer.ColumnLoaded += column => _ecology?.NoticeLoadedColumn(column);
        _fluid = new FluidSimulation(_world);
        _farTerrain = new FarTerrain(_generator, _registry, _jobs)
        {
            // Než streamer poprvé změří, kam svět sahá. Přepíše se hned prvním framem.
            NearRadius = 192f,

            // Vypínač pro A/B měření: bez něj nejde poznat, kolik z času GPU jde na LOD
            // a kolik na chunky. Ve hře se přepíná v dev menu, v selftestu jedině takhle.
            // Distant Horizon je základní součást rendereru a žádný mod ani vlastní world
            // preset ho automaticky nevypíná. Vypnout jej lze pouze výslovně diagnostickou
            // proměnnou prostředí, například při A/B měření výkonu.
            Enabled = (Environment.GetEnvironmentVariable("TESSERIS_LOD")
                ?? Environment.GetEnvironmentVariable("VOXELITY_LOD")) != "0",
        };

        int surface = _generator.SurfaceHeight(0, 0);
        _spawn = new Vector3(0.5f, surface + 2f, 0.5f);
        _player.Teleport(_spawn);
        _previousFootPosition = _player.Position;
        _cameraStepOffset = 0f;
        _camera.Position = _player.EyePosition;
        _camera.PitchDegrees = -12f;

        // Render every imported mob in a deterministic grid. This turns the GPU selftest into a
        // visual regression showcase for B3D import, skinning, texture arrays and dynamic buffers.
        if (_selftestFrames > 0 && _animals is not null)
        {
            const int columns = 8;
            for (int index = 0; index < MobDefinitions.All.Count; index++)
            {
                MobDefinition definition = MobDefinitions.All[index];
                float x = ((index % columns) - ((columns - 1) * 0.5f)) * 3.2f;
                float z = -24f - ((index / columns) * 3.8f);
                float y = _generator.SurfaceHeight((int)x, (int)z) + 1.001f;
                _animals.AddForTest(definition.Id, new Vector3(x, y, z), (uint)(7 + (index * 2)));
            }
        }

        BuildDebugMenu();
        BuildLookMenu();
        Log.Info($"Seed {seed}, dohled {_streamer.ViewDistanceChunks} chunků, povrch v počátku na y = {surface}.");
    }

    /// <summary>Připojí ukládání až po pregeneraci, aby nový prázdný svět zbytečně nečetl disk.</summary>
    private Tesseris.ModApi.ResourceId SelectedWorldPreset()
    {
        if (_worldPresets.Count > 0)
        {
            int index = Math.Clamp(_worldPresetIndex, 0, _worldPresets.Count - 1);
            return _worldPresets[index].Id;
        }

        return ConfiguredWorldPreset();
    }

    private static Tesseris.ModApi.ResourceId ConfiguredWorldPreset()
    {
        string? configured = Environment.GetEnvironmentVariable("TESSERIS_WORLD_PRESET")
            ?? Environment.GetEnvironmentVariable("VOXELITY_WORLD_PRESET");
        if (string.IsNullOrWhiteSpace(configured))
        {
            return VanillaWorldPresetFactory.PresetId;
        }

        try
        {
            return new Tesseris.ModApi.ResourceId(configured);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException(
                $"TESSERIS_WORLD_PRESET '{configured}' is not a valid namespaced resource ID.",
                exception);
        }
    }

    private void AttachWorldStorage()
    {
        if (_worldDirectory is null || _jobs is null || _registry is null || _streamer is null
            || _storage is not null)
        {
            return;
        }

        _storage = new WorldStorage(_worldDirectory, _jobs, _registry);
        _streamer.Storage = _storage;

        if (_items is not null
            && InventorySerializer.TryLoad(InventoryPath, _items, out Inventory? restoredInventory)
            && restoredInventory is not null)
        {
            _inventory = restoredInventory;
            _modGame.AttachStaticContent(_registry, _items, restoredInventory, _player, _vitals);
        }

        // Obsah pecí leží vedle světa, ne v chunku — viz Furnaces.
        int restored = _furnaces?.Load(FurnacePath) ?? 0;
        int restoredChests = _chests?.Load(ChestPath) ?? 0;
        bool restoredPower = _power?.Load(PowerPath) == true;
        int restoredAnimals = 0;
        if (_animals is not null)
        {
            try
            {
                restoredAnimals = _animals.Load(AnimalPath);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException)
            {
                Log.Warn($"Zvířata se nepodařilo načíst: {exception.Message}");
            }
        }

        if (!_ecologyLoaded && _ecology is not null)
        {
            _ecologyLoaded = true;
            if (_ecology.Load(EcologyPath, _saplings))
            {
                Log.Info($"Obnoven život lesa: {_ecology.KnownTrees} známých stromů.");
            }
        }

        if (restored > 0)
        {
            Log.Info($"Obnoveno {restored} pecí i s obsahem.");
        }

        if (restoredChests > 0)
        {
            Log.Info($"Restored {restoredChests} chests with contents.");
        }

        if (restoredPower && _power is not null)
        {
            Log.Info($"Obnovena elektrická síť: {_power.Surface.Count} povrchových kabelů, {_power.Overhead.Count} vedení.");
        }
        if (restoredAnimals > 0)
        {
            Log.Info($"Obnoveno {restoredAnimals} zvířat.");
        }

        // Kolonie až nakonec: navigaci si postaví sama v prvních ticích a lidé se do ní
        // vracejí bez kontroly pochůznosti, takže na pořadí vůči zbytku nezáleží.
        if (ColonySave.Load(_colony, ColonyPath) > 0)
        {
            Log.Info($"Obnovena kolonie: {_colony.BeltCount} pasu, {_colony.MachineCount} stroju, "
                + $"{_colony.InserterCount} vkladacu, {_colony.Colonists.Count} lidi, "
                + $"{_colony.Jobs.OpenCount} nedokoncenych ukolu, "
                + $"{_colony.ItemsOnBelts} itemu na pasech.");

            // Že se stav načetl, ještě neznamená, že linka běží. V selftestu se jí nasype
            // surovina a za chvíli se vypíše, co s ní udělala — jinak by se ověřovalo jen
            // čtení souboru. Prázdná linka stojí právem a nic nedokazuje.
            if (_selftestFrames > 0)
            {
                _loadedColonyWatch = 300;
                _loadedColonyFed = FeedLoadedMachines();
            }
        }
    }

    /// <summary>Přepne připravený svět do běžného hraní.</summary>
    private void EnterGameplay(bool captureMouse = true)
    {
        _startupStage = StartupStage.Playing;
        RestoreGameplayStreamingBudget();
        AttachWorldStorage();

        if (!captureMouse)
        {
            return;
        }

        CursorState = CursorState.Grabbed;
        _ignoreNextMouseDelta = true;
        _leftWasDown = MouseState.IsButtonDown(MouseButton.Left);
        _rightWasDown = MouseState.IsButtonDown(MouseButton.Right);

        // SUROVÝ VSTUP Z MYŠI, NE POHYB KURZORU. Musí se zapnout až po zachycení kurzoru;
        // obchází systémovou akceleraci a pohled pak zůstává lineární v pohybu ruky.
        if (SupportsRawMouseInput)
        {
            RawMouseInput = true;
        }
        else
        {
            Log.Warn("Surový vstup z myši není k dispozici, pohled bude ovlivněný akcelerací systému.");
        }
    }

    /// <summary>Otevře úvodní výběr světa a načte bezpečný seznam existujících savů.</summary>
    private void OpenStartupMenu()
    {
        string saves = Path.Combine(AppContext.BaseDirectory, "saves");

        _worldCatalog = new WorldCatalog(saves);
        _knownWorlds = _worldCatalog.Discover();
        _continueWorldIndex = 0;
        _continueWorld = _knownWorlds.Count > 0 ? _knownWorlds[0] : null;
        _mainMenu = new MainMenu();
        _startupStage = StartupStage.MainMenu;
        _startupError = string.Empty;
        CursorState = CursorState.Normal;
    }

    /// <summary>Vybere předchozí nebo další uložený svět; seznam se cyklicky uzavírá.</summary>
    private void CycleContinueWorld(int direction)
    {
        if (_knownWorlds.Count <= 1 || direction == 0)
        {
            return;
        }

        _continueWorldIndex = (_continueWorldIndex + Math.Sign(direction) + _knownWorlds.Count)
            % _knownWorlds.Count;
        _continueWorld = _knownWorlds[_continueWorldIndex];
    }

    private void CycleWorldPreset(int direction)
    {
        if (_worldPresets.Count <= 1 || direction == 0)
        {
            return;
        }

        _worldPresetIndex = (_worldPresetIndex + Math.Sign(direction) + _worldPresets.Count)
            % _worldPresets.Count;
    }

    /// <summary>
    /// Začne nový nebo existující svět. Celý herní dohled se vždy připraví před vstupem;
    /// hráč tuhle práci nemá důvod přesouvat do chvíle, kdy už běží fyzika a hraní.
    /// </summary>
    private void BeginWorld(WorldInfo world, bool existing)
    {
        Tesseris.ModApi.ResourceId presetId = world.WorldPresetId is null
            ? SelectedWorldPreset()
            : new Tesseris.ModApi.ResourceId(world.WorldPresetId);
        EnsureWorldModCompatibility(world.Directory, existing, world.Seed, presetId);
        InitializeWorld(world.Seed, world.Directory, presetId);

        // Existující svět musí číst storage už během přípravy. Kdyby se připojila až potom,
        // pregenerovaný základní terén by obsadil místo uložených hráčských změn.
        if (existing)
        {
            AttachWorldStorage();
        }

        _streamer!.ViewDistanceChunks = _playerOptions.ViewDistance;
        ConfigurePreparationStreamingBudget();
        _preparationTotal = 0;
        _preparationStarted = false;
        _startupStage = StartupStage.PreparingWorld;
        CursorState = CursorState.Normal;
    }

    /// <summary>
    /// A world must keep the exact modpack that generated it. Otherwise untouched chunks would be
    /// regenerated with different content next to already saved chunks and create hard seams or
    /// silently turn removed blocks into air.
    /// </summary>
    private void EnsureWorldModCompatibility(
        string worldDirectory,
        bool existing,
        int worldSeed,
        Tesseris.ModApi.ResourceId presetId)
    {
        ModWorldRuntime runtime = ModWorldRuntime.Create(_mods!.WorldDefinitions, worldSeed, presetId);
        WorldModLockSnapshot current = WorldModLock.Capture(
            _mods.LoadedMods,
            worldPresetId: runtime.Preset.Id.Value,
            worldDimensionId: runtime.Dimension.Id.Value,
            worldDefinitionFingerprint: runtime.Fingerprint);

        if (!existing)
        {
            WorldModLock.Write(worldDirectory, current);
            return;
        }

        WorldModLockReadResult saved = WorldModLock.Read(worldDirectory);
        WorldModLockComparison comparison = WorldModLock.Compare(saved, current);

        if (comparison.IsMatch)
        {
            if (saved.Snapshot?.WorldDefinitionFingerprint is null)
            {
                WorldModLock.Write(worldDirectory, current);
            }
            return;
        }

        // Starší vanilla světy lock neměly. První spuštění je adoptuje pod právě aktivním
        // modpackem a další změna už bude kontrolovaná.
        if (comparison.Status == WorldModLockComparisonStatus.MissingLock)
        {
            WorldModLock.Write(worldDirectory, current);
            Log.Warn($"Svět '{worldDirectory}' neměl mod lock; byl připnut k aktuálnímu modpacku.");
            return;
        }

        if (comparison.Status == WorldModLockComparisonStatus.InvalidLock)
        {
            throw new InvalidDataException(
                $"Mod lock světa je poškozený: {comparison.Error ?? "neznámá chyba"}");
        }

        string added = string.Join(", ", comparison.Added.Select(mod => mod.Id));
        string removed = string.Join(", ", comparison.Removed.Select(mod => mod.Id));
        string changed = string.Join(", ", comparison.Changed.Select(mod => mod.Current.Id));
        var reasons = new List<string>();

        if (comparison.WorldDefinitionChanged)
        {
            reasons.Add("jiný world preset, dimenze nebo definice generace");
        }

        if (added.Length > 0) { reasons.Add($"přidané: {added}"); }
        if (removed.Length > 0) { reasons.Add($"odebrané: {removed}"); }
        if (changed.Length > 0) { reasons.Add($"změněné: {changed}"); }
        if (comparison.ModApiChanged) { reasons.Add("jiná verze ModApi"); }
        if (comparison.WorldGenerationPipelineChanged) { reasons.Add("jiná verze worldgen pipeline"); }

        throw new InvalidDataException(
            "Svět vyžaduje jiný modpack (" + string.Join("; ", reasons) + ").");
    }

    /// <summary>
    /// V načítací obrazovce mají workery výrazně větší frontu a GPU smí nahrát víc meshů
    /// za snímek. Žádná fyzika ani kapaliny zatím neběží, takže výkon patří přípravě světa.
    /// </summary>
    private void ConfigurePreparationStreamingBudget()
    {
        if (_streamer is null)
        {
            return;
        }

        _streamer.GenerationsPerFrame = 2048;
        _streamer.MaxGenerationInFlight = 2048;
        _streamer.MeshesPerFrame = 128;
        _streamer.MaxMeshingInFlight = 256;
        _streamer.UploadsPerFrame = 12;
        _streamer.UploadBudgetMs = 4.0;
    }

    /// <summary>Po vstupu vrátí malé rozpočty, aby streaming nikdy necukal samotným hraním.</summary>
    private void RestoreGameplayStreamingBudget()
    {
        if (_streamer is null)
        {
            return;
        }

        _streamer.GenerationsPerFrame = 160;
        _streamer.MaxGenerationInFlight = 384;
        _streamer.MeshesPerFrame = 28;
        _streamer.MaxMeshingInFlight = 48;
        _streamer.UploadsPerFrame = 2;
        _streamer.UploadBudgetMs = 0.45;
    }

    /// <summary>Ověří formulář, založí unikátní save a začne jeho přípravu.</summary>
    private void CreateWorldFromMenu()
    {
        if (_mainMenu is null || _worldCatalog is null)
        {
            return;
        }

        int randomSeed = unchecked((int)Random.Shared.NextInt64(int.MinValue, (long)int.MaxValue + 1L));

        if (!_mainMenu.TryCreateRequest(randomSeed, out WorldCreationRequest request, out string error))
        {
            _startupError = error;
            return;
        }

        try
        {
            Tesseris.ModApi.ResourceId presetId = SelectedWorldPreset();
            WorldInfo world = _worldCatalog.Create(request.Name, request.Seed, presetId.Value);
            _startupError = string.Empty;
            BeginWorld(world, existing: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or InvalidDataException or ArgumentException or InvalidOperationException)
        {
            string prefix = (_mainMenu?.Language ?? StartupLanguage.Czech) == StartupLanguage.English
                ? "Could not create world"
                : "Svet nejde vytvorit";
            _startupError = $"{prefix}: {ex.Message}";
        }
    }

    /// <summary>Otevře vybraný save a případnou chybu přístupu ukáže přímo v menu.</summary>
    private void ContinueWorldFromMenu()
    {
        if (_continueWorld is null)
        {
            return;
        }

        try
        {
            // Stejná složka, kterou bude zakládat WorldStorage. Ověření proběhne ještě
            // před vytvořením běhového světa, takže chyba oprávnění nenechá půl inicializace.
            Directory.CreateDirectory(Path.Combine(_continueWorld.Directory, "region"));
            _startupError = string.Empty;
            BeginWorld(_continueWorld, existing: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or InvalidDataException or ArgumentException or InvalidOperationException)
        {
            string prefix = (_mainMenu?.Language ?? StartupLanguage.Czech) == StartupLanguage.English
                ? "Could not open world"
                : "Svet nejde otevrit";
            _startupError = $"{prefix}: {ex.Message}";
        }
    }

    /// <summary>Obslouží klikání a klávesy úvodního menu bez propadnutí vstupu do hry.</summary>
    private void HandleStartupInput()
    {
        KeyboardState keyboard = KeyboardState;
        bool leftMouse = MouseState.IsButtonDown(MouseButton.Left);
        bool click = leftMouse && !_startupLeftWasDown;

        bool backspace = keyboard.IsKeyDown(Keys.Backspace);
        bool tab = keyboard.IsKeyDown(Keys.Tab);
        bool enter = keyboard.IsKeyDown(Keys.Enter) || keyboard.IsKeyDown(Keys.KeyPadEnter);
        bool left = keyboard.IsKeyDown(Keys.Left);
        bool right = keyboard.IsKeyDown(Keys.Right);
        bool escape = keyboard.IsKeyDown(Keys.Escape);

        bool backspacePressed = backspace && !_startupBackspaceWasDown;
        bool tabPressed = tab && !_startupTabWasDown;
        bool enterPressed = enter && !_startupEnterWasDown;
        bool leftPressed = left && !_startupLeftKeyWasDown;
        bool rightPressed = right && !_startupRightKeyWasDown;
        bool escapePressed = escape && !_startupEscapeWasDown;

        StartupLayout layout = StartupLayoutFor(ClientSize.X, ClientSize.Y);

        if (_startupStage == StartupStage.MainMenu && _mainMenu is not null)
        {
            Vector2 pointer = MouseState.Position;

            if (_graphicsSettingsOpen)
            {
                HandleGraphicsSettingsInput(
                    layout, pointer, click, escapePressed, tabPressed, leftPressed, rightPressed);
                UpdateStartupInputEdges(leftMouse, backspace, tab, enter, left, right, escape);
                return;
            }

            if (_modManagerOpen)
            {
                HandleModManagerInput(layout, pointer, click, escapePressed);
                UpdateStartupInputEdges(leftMouse, backspace, tab, enter, left, right, escape);
                return;
            }

            if (click && layout.LanguageCzech.Contains(pointer))
            {
                _mainMenu.SetLanguage(StartupLanguage.Czech);
                _startupError = string.Empty;
            }
            else if (click && layout.LanguageEnglish.Contains(pointer))
            {
                _mainMenu.SetLanguage(StartupLanguage.English);
                _startupError = string.Empty;
            }
            else if ((click && layout.WorldLeft.Contains(pointer)) || leftPressed)
            {
                CycleContinueWorld(-1);
            }
            else if ((click && layout.WorldRight.Contains(pointer)) || rightPressed)
            {
                CycleContinueWorld(1);
            }
            else if (click && _continueWorld is not null && layout.Continue.Contains(pointer))
            {
                ContinueWorldFromMenu();
            }
            else if (click && layout.Name.Contains(pointer))
            {
                _mainMenu.Focus(MainMenuField.Name);
            }
            else if (click && layout.Seed.Contains(pointer))
            {
                _mainMenu.Focus(MainMenuField.Seed);
            }
            else if (click && layout.PresetLeft.Contains(pointer))
            {
                CycleWorldPreset(-1);
            }
            else if (click && layout.PresetRight.Contains(pointer))
            {
                CycleWorldPreset(1);
            }
            else if (click && layout.Preset.Contains(pointer))
            {
                CycleWorldPreset(1);
            }
            else if (click && layout.Mods.Contains(pointer))
            {
                _modManagerOpen = true;
                _modManagerPage = 0;
                _startupError = string.Empty;
            }
            else if (click && layout.Discord.Contains(pointer))
            {
                OpenCommunityLink(DiscordUrl);
            }
            else if (click && layout.Patreon.Contains(pointer))
            {
                OpenCommunityLink(PatreonUrl);
            }
            else if (click && layout.CreateMod.Contains(pointer))
            {
                OpenCommunityLink(CreateModUrl);
            }
            else if (click && layout.Settings.Contains(pointer))
            {
                _graphicsSettingsOpen = true;
                _graphicsSettingsRow = 0;
                _startupError = string.Empty;
            }
            else if ((click && layout.Create.Contains(pointer)) || enterPressed)
            {
                CreateWorldFromMenu();
            }

            if (backspacePressed)
            {
                _mainMenu.Backspace();
            }

            if (tabPressed)
            {
                _mainMenu.FocusNext();
            }
        }
        UpdateStartupInputEdges(leftMouse, backspace, tab, enter, left, right, escape);
    }

    private void HandleGraphicsSettingsInput(
        StartupLayout startup,
        Vector2 pointer,
        bool click,
        bool escapePressed,
        bool tabPressed,
        bool leftPressed,
        bool rightPressed)
    {
        GraphicsSettingsLayout layout = GraphicsSettingsLayoutFor(startup);
        if (escapePressed || (click && layout.Back.Contains(pointer)))
        {
            _graphicsSettingsOpen = false;
            SavePlayerOptions();
            return;
        }

        if (tabPressed)
        {
            _graphicsSettingsRow = (_graphicsSettingsRow + 1) % layout.Minus.Length;
        }

        int direction = leftPressed ? -1 : rightPressed ? 1 : 0;
        if (click)
        {
            for (int row = 0; row < layout.Minus.Length; row++)
            {
                if (layout.Minus[row].Contains(pointer))
                {
                    _graphicsSettingsRow = row;
                    direction = -1;
                    break;
                }
                if (layout.Plus[row].Contains(pointer))
                {
                    _graphicsSettingsRow = row;
                    direction = 1;
                    break;
                }
            }
        }

        if (direction == 0)
        {
            return;
        }

        AdjustGraphicsSetting(_graphicsSettingsRow, direction);
        ApplyGraphicsQuality(updateViewDistance: false);
        SavePlayerOptions();
    }

    private void AdjustGraphicsSetting(int row, int direction)
    {
        switch (row)
        {
            case 0:
            {
                int mode = _playerOptions.AutomaticGraphics ? 0 : _playerOptions.GraphicsQuality + 1;
                mode = Math.Clamp(mode + direction, 0, 3);
                if (mode == 0)
                {
                    _playerOptions.AutomaticGraphics = true;
                    SelectAutomaticGraphicsProfile();
                }
                else
                {
                    _playerOptions.ApplyGraphicsProfile(mode - 1, automatic: false);
                }
                _graphicsRestartRequired = true;
                break;
            }
            case 1:
                MarkCustomGraphics();
                _playerOptions.ViewDistance = Math.Clamp(_playerOptions.ViewDistance + direction * 2, 2, 32);
                break;
            case 2:
                MarkCustomGraphics();
                _playerOptions.MsaaSamples = StepChoice(_playerOptions.MsaaSamples, direction, 1, 2, 4);

                // Restart už potřeba není. Ve Vulkanu byl počet vzorků zapečený v render
                // passu i v pipeline, takže se musel postavit celý řetězec znovu; v OpenGL
                // stačí založit jiný cíl vykreslování.
                _graphicsRestartRequired = true;

                break;
            case 3:
                MarkCustomGraphics();
                _playerOptions.Shadows = !_playerOptions.Shadows;
                break;
            case 4:
                MarkCustomGraphics();
                _playerOptions.MapDetailSize = StepChoice(
                    _playerOptions.MapDetailSize, direction, 1024, 2048, 4096);
                _graphicsRestartRequired = true;
                break;
            case 5:
                MarkCustomGraphics();
                _playerOptions.CloudQuality = Math.Clamp(_playerOptions.CloudQuality + direction, 0, 3);
                break;
            case 6:
                MarkCustomGraphics();
                _playerOptions.FogQuality = Math.Clamp(_playerOptions.FogQuality + direction, 0, 3);
                if (_playerOptions.FogQuality > 0)
                    _playerOptions.FullView = false;
                break;
            case 7:
                MarkCustomGraphics();
                _playerOptions.Bloom = !_playerOptions.Bloom;
                break;
            case 8:
            {
                // JAS SE NEPOCITA MEZI PROFILY. Je to hrácovo pohodlí — kolik chce v noci
                // videt — ne stupen kvality, takze MarkCustomGraphics se tu nevola.
                int percent = Math.Clamp(BrightnessPercent(_playerOptions.NightBrightness) + (direction * 2), MinBrightnessPercent, 100);
                _playerOptions.NightBrightness = percent / 100f;
                if (_chunkRenderer is not null)
                {
                    _chunkRenderer.Day.NightBrightness = _playerOptions.NightBrightness;
        _chunkRenderer.Day.NightBrightnessCeiling = _playerOptions.NightBrightnessCeiling;
                }

                break;
            }
        }
    }

    /// <summary>
    /// Převede noční jas na procenta pro nastavení.
    /// </summary>
    /// <remarks>
    /// Procenta jsou přímo hodnota <c>/night</c>: 50 % je 0,5, 100 % je 1,0.
    ///
    /// <para><b>Spodní mez je 10 %, ne 50 %.</b> Vyladěná noc má 22 % a při 100 % je svět
    /// přesvětlený — celý užitečný rozsah tedy leží POD padesáti procenty. Se spodní mezí
    /// 50 % šel posuvník noc jen zesvětlovat a původní stav se jím nedal vrátit.</para>
    /// </remarks>
    private static int BrightnessPercent(float nightBrightness) =>
        Math.Clamp((int)MathF.Round(nightBrightness * 100f), MinBrightnessPercent, 100);

    /// <summary>Nejtmavší nastavitelná noc. Níž už hráč nevidí, kam šlape.</summary>
    private const int MinBrightnessPercent = 10;

    private void MarkCustomGraphics()
    {
        _playerOptions.AutomaticGraphics = false;
        _playerOptions.CustomGraphics = true;
    }

    private static int StepChoice(int current, int direction, params int[] choices)
    {
        int index = Array.IndexOf(choices, current);
        if (index < 0) index = 0;
        return choices[Math.Clamp(index + direction, 0, choices.Length - 1)];
    }

    private void OpenCommunityLink(string url)
    {
        try
        {
            _ = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            _startupError = string.Empty;
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
            _startupError = UiLanguage == StartupLanguage.English
                ? $"Could not open the browser: {exception.Message}"
                : $"Prohlizec se nepodarilo otevrit: {exception.Message}";
        }
    }

    private void UpdateStartupInputEdges(
        bool mouse,
        bool backspace,
        bool tab,
        bool enter,
        bool left,
        bool right,
        bool escape)
    {
        _startupLeftWasDown = mouse;
        _startupBackspaceWasDown = backspace;
        _startupTabWasDown = tab;
        _startupEnterWasDown = enter;
        _startupLeftKeyWasDown = left;
        _startupRightKeyWasDown = right;
        _startupEscapeWasDown = escape;
    }

    /// <summary>Každý snímek posune existující bounded streamer a hlídá konec přípravy.</summary>
    private void UpdateWorldPreparation()
    {
        if (_streamer is null)
        {
            return;
        }

        if (_streamer.Failure is not null)
        {
            _startupError = _streamer.Failure;
            return;
        }

        _phases.Begin(PhaseStreaming);
        _streamer.Update(_spawn);
        _phases.End(PhaseStreaming);

        if (_streamer.Failure is not null)
        {
            _startupError = _streamer.Failure;
            return;
        }

        int generationRemaining = _streamer.PendingGeneration + _streamer.GeneratingCount;

        if (!_preparationStarted)
        {
            _preparationStarted = true;
            _preparationTotal = Math.Max(1, generationRemaining);
        }

        bool generated = generationRemaining == 0;
        bool meshesReady = _streamer.PendingMeshing == 0 && _streamer.MeshingCount == 0;

        if (generated && meshesReady)
        {
            // Celý standardní dohled je hotový. Do hraní se nepřejde dřív, takže se první
            // pohyb hráče nemusí prát s generováním ani výrobou úvodních meshů.
            _streamer.ViewDistanceChunks = _playerOptions.ViewDistance;
            EnterGameplay();
        }
    }

    private readonly record struct UiBox(float X, float Y, float Width, float Height)
    {
        public bool Contains(Vector2 point) => point.X >= X && point.X <= X + Width
            && point.Y >= Y && point.Y <= Y + Height;
    }

    private readonly record struct PauseLayout(
        UiBox Panel,
        UiBox Resume,
        UiBox Options,
        UiBox SaveAndQuit,
        UiBox Back,
        UiBox SettingsArea);

    private static PauseLayout PauseLayoutFor(int width, int height)
    {
        float panelWidth = MathF.Min(560f, MathF.Max(420f, width - 40f));
        float panelHeight = MathF.Min(560f, MathF.Max(440f, height - 40f));
        float x = (width - panelWidth) * 0.5f;
        float y = (height - panelHeight) * 0.5f;
        return new PauseLayout(
            new UiBox(x, y, panelWidth, panelHeight),
            new UiBox(x + 70f, y + 130f, panelWidth - 140f, 54f),
            new UiBox(x + 70f, y + 204f, panelWidth - 140f, 54f),
            new UiBox(x + 70f, y + 278f, panelWidth - 140f, 54f),
            new UiBox(x + 70f, y + panelHeight - 72f, panelWidth - 140f, 46f),
            new UiBox(x + 42f, y + 100f, panelWidth - 84f, panelHeight - 190f));
    }

    private static UiBox OptionMinus(PauseLayout layout, int row) => new(
        layout.SettingsArea.X,
        layout.SettingsArea.Y + row * 52f,
        46f,
        42f);

    private static UiBox OptionPlus(PauseLayout layout, int row) => new(
        layout.SettingsArea.X + layout.SettingsArea.Width - 46f,
        layout.SettingsArea.Y + row * 52f,
        46f,
        42f);

    private void HandlePauseMenuInput()
    {
        bool left = MouseState.IsButtonDown(MouseButton.Left);
        bool click = left && !_pauseLeftWasDown;
        _pauseLeftWasDown = left;
        if (!click) return;

        PauseLayout layout = PauseLayoutFor(ClientSize.X, ClientSize.Y);
        Vector2 pointer = MouseState.Position;

        if (!_optionsMenuOpen)
        {
            if (layout.Resume.Contains(pointer)) ResumeGame();
            else if (layout.Options.Contains(pointer)) _optionsMenuOpen = true;
            else if (layout.SaveAndQuit.Contains(pointer))
            {
                SavePlayerOptions();
                _restartToMainMenuAfterUnload = true;
                Close();
            }
            return;
        }

        if (layout.Back.Contains(pointer))
        {
            _optionsMenuOpen = false;
            SavePlayerOptions();
            return;
        }

        int direction = 0;
        int rowHit = -1;
        for (int row = 0; row < 9; row++)
        {
            if (OptionMinus(layout, row).Contains(pointer)) { direction = -1; rowHit = row; break; }
            if (OptionPlus(layout, row).Contains(pointer)) { direction = 1; rowHit = row; break; }
        }
        if (rowHit < 0) return;

        switch (rowHit)
        {
            case 0:
                _mouseSensitivity = Math.Clamp(_mouseSensitivity + direction * 0.005f, 0.005f, 0.3f);
                break;
            case 1:
                _camera.FieldOfViewDegrees = Math.Clamp(_camera.FieldOfViewDegrees + direction * 5f, 50f, 110f);
                break;
            case 2 when _streamer is not null:
                _streamer.ViewDistanceChunks = Math.Clamp(_streamer.ViewDistanceChunks + direction * 2, 2, 32);
                break;
            case 3:
                _playerOptions.MasterVolume = Math.Clamp(_playerOptions.MasterVolume + direction * 0.1f, 0f, 1f);
                _audio?.SetGlobalVolume(_playerOptions.MasterVolume);
                break;
            case 4:
                _vsync = !_vsync;
                ApplyVsync();
                break;
            case 5:
                _playerOptions.FullView = !_playerOptions.FullView;
                break;
            case 6:
                _playerOptions.GraphicsQuality = Math.Clamp(
                    _playerOptions.GraphicsQuality + direction,
                    PlayerOptions.LowGraphics,
                    PlayerOptions.HighGraphics);
                ApplyGraphicsQuality(updateViewDistance: true);
                break;
        }
        SavePlayerOptions();
    }

    private void SavePlayerOptions()
    {
        _playerOptions.MouseSensitivity = _mouseSensitivity;
        _playerOptions.FieldOfView = _camera.FieldOfViewDegrees;
        _playerOptions.ViewDistance = _streamer?.ViewDistanceChunks ?? _playerOptions.ViewDistance;
        _playerOptions.VSync = _vsync;
        try { _playerOptions.Save(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Nastavení nešlo uložit: {exception.Message}");
        }
    }

    private void ApplyGraphicsQuality(bool updateViewDistance)
    {
        int quality = Math.Clamp(
            _playerOptions.GraphicsQuality,
            PlayerOptions.LowGraphics,
            PlayerOptions.HighGraphics);

        if (updateViewDistance)
        {
            _playerOptions.ApplyGraphicsProfile(quality, automatic: false);
            if (_streamer is not null)
            {
                _streamer.ViewDistanceChunks = _playerOptions.ViewDistance;
            }
        }

        if (_chunkRenderer is not null)
        {
            _chunkRenderer.Bloom = _playerOptions.Bloom;
            _chunkRenderer.Fxaa = _playerOptions.Fxaa;
        _chunkRenderer.FancyWater = _playerOptions.FancyWater;
        _chunkRenderer.Day.NightBrightness = _playerOptions.NightBrightness;
        _chunkRenderer.Day.NightBrightnessCeiling = _playerOptions.NightBrightnessCeiling;
            _chunkRenderer.CloudQuality = QualityFactor(_playerOptions.CloudQuality);
            _chunkRenderer.FogQuality = QualityFactor(_playerOptions.FogQuality);
        }
    }

    private static float QualityFactor(int quality) => quality switch
    {
        <= 0 => 0f,
        1 => 0.3f,
        2 => 0.6f,
        _ => 1f,
    };

    private readonly record struct StartupLayout(
        UiBox Panel,
        UiBox LanguageCzech,
        UiBox LanguageEnglish,
        UiBox WorldLeft,
        UiBox Continue,
        UiBox WorldRight,
        UiBox Name,
        UiBox Seed,
        UiBox PresetLeft,
        UiBox Preset,
        UiBox PresetRight,
        UiBox Pregen,
        UiBox Create,
        UiBox Mods,
        UiBox Discord,
        UiBox Patreon,
        UiBox CreateMod,
        UiBox Settings,
        UiBox Info);

    /// <summary>Jedna sada souřadnic sdílená kreslením i hit-testem tlačítek.</summary>
    private static StartupLayout StartupLayoutFor(int screenWidth, int screenHeight)
    {
        float panelWidth = MathF.Min(1120f, MathF.Max(640f, screenWidth - 32f));
        float panelHeight = MathF.Min(688f, MathF.Max(560f, screenHeight - 32f));
        float panelX = (screenWidth - panelWidth) * 0.5f;
        float panelY = MathF.Max(16f, (screenHeight - panelHeight) * 0.5f);
        float x = panelX + 32f;
        float innerWidth = panelWidth - 64f;
        float formWidth = Math.Clamp(innerWidth * 0.48f, 280f, 500f);
        float infoX = x + formWidth + 28f;
        float infoWidth = MathF.Max(220f, panelX + panelWidth - 32f - infoX);
        const float communityGap = 8f;
        float communityWidth = (infoWidth - communityGap * 4f) / 5f;
        float communityY = panelY + panelHeight - 66f;

        return new StartupLayout(
            new UiBox(panelX, panelY, panelWidth, panelHeight),
            new UiBox(panelX + panelWidth - 190f, panelY + 24f, 88f, 28f),
            new UiBox(panelX + panelWidth - 94f, panelY + 24f, 88f, 28f),
            new UiBox(x, panelY + 104f, 48f, 48f),
            new UiBox(x + 56f, panelY + 104f, formWidth - 112f, 48f),
            new UiBox(x + formWidth - 48f, panelY + 104f, 48f, 48f),
            new UiBox(x, panelY + 258f, formWidth, 44f),
            new UiBox(x, panelY + 342f, formWidth, 44f),
            new UiBox(x, panelY + 430f, 48f, 44f),
            new UiBox(x + 56f, panelY + 430f, formWidth - 112f, 44f),
            new UiBox(x + formWidth - 48f, panelY + 430f, 48f, 44f),
            new UiBox(x, panelY + 502f, formWidth, 44f),
            new UiBox(x, panelY + 574f, formWidth, 52f),
            new UiBox(infoX, communityY, communityWidth, 42f),
            new UiBox(infoX + communityWidth + communityGap, communityY, communityWidth, 42f),
            new UiBox(infoX + (communityWidth + communityGap) * 2f, communityY, communityWidth, 42f),
            new UiBox(infoX + (communityWidth + communityGap) * 3f, communityY, communityWidth, 42f),
            new UiBox(infoX + (communityWidth + communityGap) * 4f, communityY, communityWidth, 42f),
            new UiBox(infoX, panelY + 78f, infoWidth, panelHeight - 156f));
    }

    private readonly record struct GraphicsSettingsLayout(UiBox Back, UiBox[] Minus, UiBox[] Plus);

    private static GraphicsSettingsLayout GraphicsSettingsLayoutFor(StartupLayout startup)
    {
        // Devet radku: osm grafickych a jas. Musi souhlasit s poli popisku a hodnot niz.
        const int rows = 9;
        var minus = new UiBox[rows];
        var plus = new UiBox[rows];
        float left = startup.Panel.X + 70f;
        float right = startup.Panel.X + startup.Panel.Width - 70f;
        float firstY = startup.Panel.Y + 126f;
        float step = MathF.Min(58f, (startup.Panel.Height - 210f) / rows);
        for (int row = 0; row < rows; row++)
        {
            float y = firstY + row * step;
            minus[row] = new UiBox(left, y, 48f, 42f);
            plus[row] = new UiBox(right - 48f, y, 48f, 42f);
        }

        return new GraphicsSettingsLayout(
            new UiBox(startup.Panel.X + startup.Panel.Width - 170f, startup.Panel.Y + 26f, 130f, 40f),
            minus,
            plus);
    }

    private const int VisibleModRows = 8;

    private readonly record struct ModManagerLayout(
        UiBox Back,
        UiBox Apply,
        UiBox Previous,
        UiBox Next,
        UiBox List);

    private static ModManagerLayout ModManagerLayoutFor(StartupLayout startup) => new(
        new UiBox(startup.Panel.X + 32f, startup.Panel.Y + 82f, 150f, 38f),
        new UiBox(startup.Panel.X + startup.Panel.Width - 292f, startup.Panel.Y + 82f, 260f, 38f),
        new UiBox(startup.Panel.X + 32f, startup.Panel.Y + startup.Panel.Height - 54f, 90f, 34f),
        new UiBox(startup.Panel.X + startup.Panel.Width - 122f, startup.Panel.Y + startup.Panel.Height - 54f, 90f, 34f),
        new UiBox(startup.Panel.X + 32f, startup.Panel.Y + 140f, startup.Panel.Width - 64f, 416f));

    private static UiBox ModRow(ModManagerLayout layout, int index)
    {
        const float gap = 6f;
        float height = (layout.List.Height - (VisibleModRows - 1) * gap) / VisibleModRows;
        return new UiBox(layout.List.X, layout.List.Y + index * (height + gap), layout.List.Width, height);
    }

    private int ModManagerPageCount => Math.Max(
        1,
        ((_modManager?.Mods.Count ?? 0) + VisibleModRows - 1) / VisibleModRows);

    private void HandleModManagerInput(StartupLayout startup, Vector2 pointer, bool click, bool escapePressed)
    {
        if (_modManager is null) return;
        ModManagerLayout layout = ModManagerLayoutFor(startup);
        if (escapePressed || (click && layout.Back.Contains(pointer)))
        {
            _modManagerOpen = false;
            return;
        }

        if (click && layout.Previous.Contains(pointer))
        {
            _modManagerPage = Math.Max(0, _modManagerPage - 1);
            return;
        }
        if (click && layout.Next.Contains(pointer))
        {
            _modManagerPage = Math.Min(ModManagerPageCount - 1, _modManagerPage + 1);
            return;
        }
        if (click && layout.Apply.Contains(pointer) && _modManager.HasPendingChanges)
        {
            ApplyModChangesAndRestart();
            return;
        }

        int start = _modManagerPage * VisibleModRows;
        IReadOnlyList<InstalledModInfo> mods = _modManager.Mods;
        for (int row = 0; row < VisibleModRows && start + row < mods.Count; row++)
        {
            if (click && ModRow(layout, row).Contains(pointer))
            {
                _modManager.Toggle(mods[start + row].Id);
                _startupError = string.Empty;
                return;
            }
        }
    }

    private void ApplyModChangesAndRestart()
    {
        if (_modManager is null || !_modManager.HasPendingChanges) return;
        try
        {
            _modManager.Save();
            string processPath = Environment.ProcessPath
                ?? throw new InvalidOperationException("The current executable path is unavailable.");
            var start = new ProcessStartInfo(processPath)
            {
                UseShellExecute = true,
                WorkingDirectory = Environment.CurrentDirectory,
            };
            string[] currentArguments = Environment.GetCommandLineArgs();
            if (string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
            {
                start.ArgumentList.Add(currentArguments[0]);
            }
            foreach (string argument in currentArguments.Skip(1))
            {
                start.ArgumentList.Add(argument);
            }
            _ = Process.Start(start) ?? throw new InvalidOperationException("The restarted process did not start.");
            Close();
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or InvalidOperationException)
        {
            _startupError = $"Mod settings could not be applied: {exception.Message}";
        }
    }

    protected override void OnUpdateFrame(FrameEventArgs args)
    {
        base.OnUpdateFrame(args);

        _frameTimer.BeginFrame();
        _stats.BeginFrame();
        _phases.BeginFrame();
        _tickPhases.BeginFrame();
        SnapshotGcCounts();

        if (_selftestFrames > 0)
        {
            AttributePreviousFrameToGc();
            RunSelftestSchedule();

            if (_frameTimer.FrameCount >= _selftestFrames)
            {
                Close();
                return;
            }
        }
        else if (_startupStage != StartupStage.Playing)
        {
            HandleStartupInput();

            if (_startupStage == StartupStage.PreparingWorld)
            {
                UpdateWorldPreparation();
            }

            // Menu ani příprava nespouští fyziku, životy, kapaliny či růst. Streamer se
            // během přípravy obsluhuje přímo v UpdateWorldPreparation.
            return;
        }
        else
        {
            HandleInput((float)_frameTimer.DeltaSeconds);
            if (_pauseMenuOpen)
            {
                // Skutečná pauza: neběží fyzika, čas, mody, kapaliny ani streaming.
                return;
            }
        }

        // MODY ZATÍM ZŮSTÁVAJÍ NA DÉLCE SNÍMKU. Mají vlastní 20Hz hodiny a podle rozsahu v1
        // celá vrstva odejde, takže se do nich nevyplatí sahat.
        if (_mods is not null)
        {
            _mods.AdvanceSimulation(
                TimeSpan.FromSeconds(_frameTimer.DeltaSeconds),
                (tick, delta) => _modEntities?.RunSystems(tick, delta));
            _modTick = _mods.SimulationClock.Tick;
        }

        _modAudioBridge?.ProcessPending();
        _modRenderBridge?.Update(TimeSpan.FromSeconds(_frameTimer.DeltaSeconds));

        // ODPOČTY V UI JDOU PO SNÍMCÍCH, ne po tikách. Jsou to hlášky na obrazovce, ne herní
        // stav — kdyby se počítaly v tiku, zpomalená simulace by je nechala viset déle.
        float frameSeconds = (float)_frameTimer.DeltaSeconds;
        _modCommandMessageSeconds = Math.Max(0f, _modCommandMessageSeconds - frameSeconds);
        _powerNoticeSeconds = Math.Max(0f, _powerNoticeSeconds - frameSeconds);

        // STREAMING PŘED SIMULACÍ. Kapaliny sahají na chunky, které musí být načtené;
        // opačné pořadí by počítalo vodu v chuncích, které se za okamžik zahodí. Streamer
        // proto dostane polohu kamery ještě z minulého snímku — při 60 a víc snímcích je
        // to posun v řádu centimetrů a dohled má rezervu na víc.
        _phases.Begin(PhaseStreaming);
        _streamer?.Update(_camera.Position);
        _phases.End(PhaseStreaming);

        if (_streamer is not null)
        {
            // Streamer si měří sám; tady se to jen přelije do rozpadu po fázích, aby šlo
            // vidět p50 a p99, ne jen maximum.
            _phases.Add(PhaseUpload, _streamer.LastUploadMs);
            _phases.Add(PhaseSchedule, _streamer.LastScheduleMs);
        }

        // ================= PEVNÝ TICK =================
        _ticksLastFrame = _simClock.Advance(TimeSpan.FromSeconds(_frameTimer.DeltaSeconds));
        while (_simClock.TryConsumeTick())
        {
            SimulateTick();
        }

        // ================= PER-FRAME: VIZUÁL =================
        //
        // Kamera se vyhlazuje po snímcích schválně. Je to obrazová věc, ne herní stav —
        // svázat ji s tikem by znamenalo, že se při zpomalené simulaci zpomalí i dorovnání
        // pohledu, což vypadá jako zásek.
        //
        // Odečet výstupu na schod je naopak V TIKU: StepRiseThisUpdate se nuluje uvnitř
        // PlayerController.Update, takže odečítat ho po snímcích znamenalo při 300 FPS
        // odečíst tentýž schod pětkrát a strhnout kameru na dolní mez. Tady zůstává jen
        // vyhlazování, které je obrazové.
        _cameraStepOffset = Math.Clamp(_cameraStepOffset, -PlayerController.StepHeight, 0f);
        float cameraStepBlend = 1f - MathF.Exp(-CameraStepSmoothing * frameSeconds);
        _cameraStepOffset += (0f - _cameraStepOffset) * cameraStepBlend;

        // RENDER INTERPOLUJE MEZI TIKY (pravidlo 6.6). Poloha hráče se mění jen 60× za
        // vteřinu, ale snímků se kreslí víc — bez tohohle kamera několik snímků stojí
        // a pak skočí, což se čte jako trhání. Interpoluje se mezi stavem na začátku
        // a na konci posledního tiku, takže se obraz nikdy nepředbíhá před simulaci.
        _camera.Position = InterpolatedEye() + new Vector3(0f, _cameraStepOffset, 0f);

        // FÁZE KROKU POSTAVY. Roste s ušlou vodorovnou vzdáleností, ne s časem — jinak by
        // figurka "šlapala" i když hráč stojí, a při sprintu by kmitala stejně jako při chůzi.
        // Z INTERPOLOVANÉ POLOHY, ne z tik-kvantované. Ve snímku, ve kterém neproběhl tik,
        // byl posun přesně nula — a fáze se tím nulovala. Postava pak asi v polovině snímků
        // spadla do klidové animace a chůze se rozblikala natolik, že nebylo poznat, že jde.
        Vector3 drawFoot = InterpolatedFoot();
        Vector3 travel = drawFoot - _previousDrawFoot;
        _previousDrawFoot = drawFoot;
        travel.Y = 0f;
        _playerWalkPhase = travel.Length > 0.0005f
            ? (_playerWalkPhase + (travel.Length * 6.5f)) % MathF.Tau
            : 0f;

        // ČAS ANIMACÍ SE POSOUVÁ VŽDYCKY, ne až ve třetí osobě. Byl schovaný v ApplyCameraView
        // za návratem pro první osobu, takže se ve hře nehýbal — a všichni kolonisté renderovali
        // jeden zamrzlý snímek s roztaženýma rukama a nohama. Každý jiný, protože mají fázi
        // posunutou podle indexu, takže to vypadalo, že některým se animace „nespustila".
        //
        // Je to potřetí táž chyba: hodnota, kterou potřebuje celý svět, nastavená ve větvi
        // pro kameru. Předtím PlayerSkinLayer a před ním umístění ruky.
        _playerAnimationTime += (float)_frameTimer.DeltaSeconds;

        ApplyCameraView();

        // RUKA AŽ TEĎ, ne v tiku. Zapéká si do sebe polohu a všechny tři osy kamery, takže
        // spočítaná v tiku dostala kameru z minulého snímku — a byla o celý odsimulovaný
        // krok pozadu. Ruka stojí 0,58 m před okem, near plane je 0,12 m, takže jakmile se
        // oko posunulo o víc než 0,46 m, celá se ořízla. Let se sprintem dělá 0,81 m za tik,
        // což je přesně to hlášené "při letu ruka zmizí".
        UpdateHeld();
        MeasureInterpolation();

        // Posluchač musí sledovat hlavu, jinak by se směr ani hlasitost zvuků neměnily.
        _audio?.SetListener(_camera.Position, _camera.Forward, _camera.Up, _player.Velocity);

        UpdateFootsteps();

        _phases.Begin(PhaseFarTerrain);
        UpdateFarTerrain();
        _phases.End(PhaseFarTerrain);

        UpdateDiagnostics();

        // DO HISTORIE JEN SNÍMKY, VE KTERÝCH SE OPRAVDU TIKALO. Při 400 snímcích za vteřinu
        // a 60 tikách nespustí tik pět snímků ze šesti; kdyby se zapisovaly i ty, byl by
        // medián nula a tvrdil by, že simulace nestojí nic. Takhle čísla znamenají cenu
        // simulační práce v tom snímku, kdy k ní došlo.
        if (_ticksLastFrame > 0)
        {
            _tickPhases.EndFrame();
        }
    }

    /// <summary>
    /// Jeden krok simulace. Vždy přesně 1/60 vteřiny, bez ohledu na snímkovou frekvenci.
    /// </summary>
    /// <remarks>
    /// <para>Sem patří všechno, co je herní stav. Co je jen vidět — kamera, vyhlazení,
    /// odpočty hlášek — zůstává v <see cref="OnUpdateFrame"/> po snímcích.</para>
    ///
    /// <para><b>Pořadí uvnitř tiku je významné</b> a přeneslo se beze změny: hráč se pohne,
    /// pak se počítají ležící předměty (jinak by se přitahovaly k místu, kde stál minule),
    /// pak životy (zranění z pádu se účtuje v okamžiku dopadu) a teprve pak zvířata, aby
    /// jejich útok nepřepsalo <c>Vitals.Update</c>.</para>
    /// </remarks>
    private void SimulateTick()
    {
        WatchLoadedColony();

        _tickPhases.Begin(TickPhaseTotal);
        float step = _simClock.StepSeconds;

        // POHYB HRÁČE. Vstup se navzorkoval v HandleInput při snímku, tady se uplatní pevným
        // krokem. _movementBlocked drží gravitaci, dokud pod hráčem není načtený chunk.
        _tickPhases.Begin(TickPhaseEntities);

        // STAV PŘED POHYBEM. Odsud interpoluje render, viz InterpolatedEye.
        _previousEyePosition = _player.EyePosition;
        _previousTickFoot = _player.Position;
        _hasPreviousEye = true;

        if (_world is not null && !_movementBlocked)
        {
            _player.Update(
                _world,
                _movementIntent.Wish,
                jump: _movementIntent.Jump,
                sprint: _movementIntent.Sprint,
                verticalWish: _movementIntent.VerticalWish,
                step,
                crouch: _movementIntent.Crouch);

            // Každý výstup na schod se smí odečíst právě jednou, tedy tady. Po snímcích
            // se tatáž hodnota odečítala tolikrát, kolik bylo snímků na jeden tik.
            _cameraStepOffset -= _player.StepRiseThisUpdate;
        }

        // LEŽÍCÍ PŘEDMĚTY. Padají, slévají se a přiblížením se seberou — proto se posouvají
        // po pohybu hráče, ne před ním.
        if (_world is not null && _inventory is not null && _drops is not null && _drops.Count > 0)
        {
            _drops.Update(_world, _player, _inventory, step);
        }

        UpdateBuildingPartAnimations(step);
        _tickPhases.End(TickPhaseEntities);

        DayCycle? day = _chunkRenderer?.Day;

        // DENNÍ ČAS V TIKU. Dřív se posouval při kreslení, takže rychlost dne závisela na
        // snímkové frekvenci — na výkonnějším stroji utíkal čas rychleji.
        if (day is not null && (_selftestFrames > 0 || (_startupStage == StartupStage.Playing && !_pauseMenuOpen)))
        {
            day.Update(step);
        }

        float biologicalSeconds = day?.BiologicalSeconds(step) ?? step;
        float growthSeconds = day?.GrowthSeconds(step) ?? step;
        float natureSeconds = step * (day?.NatureSpeed ?? 1f);

        _tickPhases.Begin(TickPhaseMachines);

        // TLENÍ LISTÍ. Koruna bez kmene se během pár vteřin rozpadne a pustí, co v ní bylo.
        if (_world is not null && _drops is not null && _streamer is not null && _felling is not null)
        {
            _felling.Update(_world, _drops, _streamer, biologicalSeconds);
            _furnaces?.Update(step);
        }

        _tickPhases.End(TickPhaseMachines);

        _tickPhases.Begin(TickPhaseNature);

        // RŮST SAZENIC. Uzavírá smyčku les → dřevo → les.
        if (_world is not null && _streamer is not null && _saplings is not null)
        {
            foreach (Vector3i chunk in _saplings.Update(
                _world,
                _player.Position,
                growthSeconds,
                realSeconds: step))
            {
                _streamer.Invalidate(chunk);
            }

            if (_ecology is not null && _felling is not null)
            {
                foreach (Vector3i root in _saplings.GrownThisUpdate)
                {
                    _ecology.NoticeTree(root);
                }

                _ecology.Update(
                    _world,
                    _player.Position,
                    biologicalSeconds,
                    _saplings,
                    _felling,
                    block => _streamer.InvalidateBlock(block.X, block.Y, block.Z),
                    realSeconds: natureSeconds,
                    growthAllowed: day?.IsDay ?? true,
                    wallSeconds: step);
            }
        }

        _tickPhases.End(TickPhaseNature);

        _tickPhases.Begin(TickPhaseEntities);

        // ŽIVOTY AŽ PO POHYBU, ZVÍŘATA AŽ PO ŽIVOTECH.
        if (_world is not null)
        {
            _vitals.Update(_world, _player, _inventory, _creative, step);

            _animals?.Update(
                _world,
                _player.Position,
                step,
                ignorePlayer: _creative,
                allowHostileSpawns: !(_chunkRenderer?.Day.IsDay ?? true),
                damagePlayer: damage => _vitals.Hurt(damage, ignoreArmour: false, armour: _inventory));

            if (_vitals.Dead)
            {
                Respawn();
            }
        }

        AdvanceSwing(step);
        _tickPhases.End(TickPhaseEntities);

        // KOLONIE. Navigace se doplňuje s rozpočtem, protože jedna mřížka stojí 3,34 ms.
        _tickPhases.Begin(TickPhasePathfinding);
        if (_world is not null && _streamer is not null)
        {
            _colony.SetWater(_world.Registry.IndexOf("tesseris:water"));
            ResolveColonyFoodOnce();

            // ROZPAD UVNITŘ KOLONIE. Celý tik hlásil p99 18,9 ms, ale rozpad po kategoriích
            // ukazoval 1,2 ms — tedy sedmnáct milisekund se nikde neobjevilo. Tohle je ta
            // nejpodezřelejší dvojice: stavba navigační mřížky stojí naměřených 3,34 ms na
            // chunk a hledání cesty 0,858 ms, takže obojí patří změřit zvlášť.
            long navigaceOd = System.Diagnostics.Stopwatch.GetTimestamp();
            _colony.UpdateNavigation(_world, _world.Registry, _player.Position);
            _tickNavigationMs = Msec(navigaceOd);

            TryWelcomeColonist();
            TryMarkProbeArea();
            TryRunFactoryProbe();
            TryRunHungerProbe();

            // KOLONISTÉ SE VYHÝBAJÍ HRÁČI. Nedá se to obrátit — zastavit hráče o kolonisty by
            // znamenalo sáhnout na PlayerController, a hlavně by dav, který hráče zazdí
            // v koutě, byl horší než dav, který se rozestoupí. Poloha se předává každý tik,
            // protože hráč se hýbe rychleji než oni.
            _colony.Colonists.PlayerPosition = _player.Position;

            long koloniOd = System.Diagnostics.Stopwatch.GetTimestamp();
            _colony.Tick(_world, _world.Registry);
            _tickColonyMs = Msec(koloniOd);

            // NEJHORŠÍ ZA BĚH, ne poslední hodnota. Špička je přesně to, co dělá p99.
            _worstNavigationMs = Math.Max(_worstNavigationMs, _tickNavigationMs);
            _worstColonyMs = Math.Max(_worstColonyMs, _tickColonyMs);
            _worstGridMs = Math.Max(_worstGridMs, _colony.LastGridMs);
            _worstPortalsMs = Math.Max(_worstPortalsMs, _colony.LastPortalsMs);
            _worstCollectMs = Math.Max(_worstCollectMs, _colony.LastCollectMs);

            // SONDA V TIKU, NE VE SNÍMKU. Měří se posun za jeden simulační krok; ve snímku
            // by se tentýž tik započítal tolikrát, kolik jich obrazovka stihne, a „posun za
            // tik" by vyšel nula pokaždé, když se zrovna netikalo.
            MeasureColonistBodies();
        }

        _tickPhases.End(TickPhasePathfinding);

        _tickPhases.Begin(TickPhaseFluid);
        if (_fluid is not null && _streamer is not null)
        {
            foreach (Vector3i changed in _fluid.Update(step))
            {
                _streamer.Invalidate(changed);
            }
        }

        _tickPhases.End(TickPhaseFluid);
        _tickPhases.End(TickPhaseTotal);
    }

    /// <summary>Kolik milisekund uplynulo od zadaného razítka.</summary>
    private static double Msec(long from) =>
        (System.Diagnostics.Stopwatch.GetTimestamp() - from) * 1000.0
        / System.Diagnostics.Stopwatch.Frequency;

    /// <summary>Nejhorší naměřená stavba navigace v jednom tiku. Jen pro rozpad v selftestu.</summary>
    private double _tickNavigationMs;

    /// <summary>Nejhorší naměřený tik kolonie (lidé, pásy, stroje). Rozpad v selftestu.</summary>
    private double _tickColonyMs;

    /// <summary>Nejhorší hodnoty za celý běh. Průměr by špičku schoval.</summary>
    private double _worstNavigationMs;

    /// <inheritdoc cref="_worstNavigationMs"/>
    private double _worstColonyMs;

    /// <summary>Rozpad navigace: stavba mřížky, portály, hledání chybějících chunků.</summary>
    private double _worstGridMs;

    /// <inheritdoc cref="_worstGridMs"/>
    private double _worstPortalsMs;

    /// <inheritdoc cref="_worstGridMs"/>
    private double _worstCollectMs;

    /// <summary>
    /// Naseje kolem hráče zadaný počet instancovaných itemů.
    /// </summary>
    /// <remarks>
    /// <para>Slouží k měření povinného benchmarku T4 („20 000 itemů, 60 FPS, draw cally
    /// v jednotkách"). Pásy ve světě zatím nestojí, takže se itemy rozmístí do mřížky —
    /// pro renderer je to totéž, jen se nehýbou.</para>
    ///
    /// <para>Zapíná se příkazem <c>/items N</c> nebo proměnnou <c>VOXELITY_ITEMS</c>, aby
    /// se to dalo změřit i v selftestu, který příkazy neumí. Ve výchozím stavu je vypnuté
    /// a nestojí nic.</para>
    /// </remarks>
    private void FillInstancedItemProbe()
    {
        if (_chunkRenderer is null)
        {
            return;
        }

        _chunkRenderer.BeginInstancedItems();

        DrawColonyInstances();

        if (_instancedItemProbe <= 0)
        {
            return;
        }

        // Mřížka kolem hráče. Krok 1,5 bloku, aby se kostky nepřekrývaly a bylo poznat,
        // že jich je opravdu tolik.
        int side = (int)MathF.Ceiling(MathF.Cbrt(_instancedItemProbe));
        Vector3 origin = _player.Position - new Vector3(side * 0.75f, 0f, side * 0.75f);

        int placed = 0;
        for (int y = 0; y < side && placed < _instancedItemProbe; y++)
        {
            for (int z = 0; z < side && placed < _instancedItemProbe; z++)
            {
                for (int x = 0; x < side && placed < _instancedItemProbe; x++)
                {
                    var centre = new Vector3(
                        origin.X + (x * 1.5f),
                        origin.Y + 1.5f + (y * 1.5f),
                        origin.Z + (z * 1.5f));

                    // Bez cullingu jen pro měření: ukáže skutečnou cenu 20 000 kreslených
                    // instancí, kdežto s cullingem jich v záběru je zlomek.
                    if (_probeSkipCulling)
                    {
                        _chunkRenderer.AddInstancedItemUnculled(centre, 0.35f, _probeItemLayer, 1f);
                    }
                    else
                    {
                        _chunkRenderer.AddInstancedItem(_frustum, centre, 0.35f, _probeItemLayer, 1f);
                    }
                    placed++;
                }
            }
        }
    }

    /// <summary>Kolik instancovaných itemů se má naset kolem hráče. Nula = vypnuto.</summary>
    /// <remarks>
    /// Čte se z <c>VOXELITY_ITEMS</c>, aby šlo měřit i v selftestu, který příkazy neumí.
    /// </remarks>
    private int _instancedItemProbe = ParseProbeCount();

    /// <summary>Vrstva textury pro itemy sondy. Jednička je první skutečná dlaždice atlasu.</summary>
    private readonly float _probeItemLayer = 1f;

    /// <summary>Kolonisté připravení ke kreslení. Plní se každý snímek, nealokuje se.</summary>
    private readonly List<(Vector3 Position, float Yaw, float Frame)> _colonistPoses = [];

    /// <summary>Nejvíc kolonistů, kteří byli za celý běh naráz v záběru. Pro selftest.</summary>
    private int _colonistPosesPeak;

    /// <summary>Jak rychle se kolonista otáčí, v radiánech za vteřinu.</summary>
    /// <remarks>Zhruba půl otáčky za vteřinu: dost svižně, aby nezaostával, dost pomalu, aby to nebyl skok.</remarks>
    private const float ColonistTurnRate = 3.2f;

    /// <summary>Kam který kolonista naposledy koukal. Bez toho se otočí zpátky, jakmile stojí.</summary>
    private float[] _colonistYaw = [];

    /// <summary>
    /// Postaví kolonisty do dávky postav.
    /// </summary>
    /// <remarks>
    /// <para><b>Interpoluje se mezi dvěma SPOJITÝMI polohami, ne mezi buňkami.</b> Kolonista
    /// má fyzické tělo a hýbe se každý tik o kus; render doplňuje jen ten zlomek mezi tikem
    /// a snímkem, přesně jako u kamery. Dřív se lerpovalo mezi středy buněk podle postupu
    /// kroku, což byl jediný důvod, proč chůze nevypadala jako sekaná — a zároveň důvod,
    /// proč vypadala jako šachovnice.</para>
    ///
    /// <para><b>Skok se neinterpoluje</b>. Načtení savu i přesun na jiné
    /// místo posunou kolonistu o víc, než kolik za tik ujde; přes takový rozdíl se lerpovat
    /// nesmí, jinak postava plynule přeletí přes půl světa. Práh se testuje na vzdálenosti,
    /// ne na výčtu volajících — stejně jako u kamery.</para>
    ///
    /// <para><b>Natočení se drží.</b> Kdo stojí, kouká tam, kam šel naposledy; jinak by se
    /// každý stojící kolonista srovnal na sever.</para>
    ///
    /// <para><b>Kdo není v záběru, nemá geometrii</b> (pravidlo 6.7). Postava stojí CPU čas
    /// za skládání mesh, takže cullovat se musí dřív, ne až v shaderu.</para>
    /// </remarks>
    private void FillColonistPoses()
    {
        _colonistPoses.Clear();

        if (_chunkRenderer is null)
        {
            return;
        }

        ColonySimulation people = _colony.Colonists;
        if (_colonistYaw.Length < people.Count)
        {
            Array.Resize(ref _colonistYaw, Math.Max(people.Count, 64));
        }

        float alpha = (float)_simClock.InterpolationAlpha;

        for (int id = 0; id < people.Count; id++)
        {
            Vector3 to = people.PositionOf(id);
            Vector3 from = people.PreviousPositionOf(id);

            Vector3 position = (to - from).LengthSquared > MaxInterpolatedStep * MaxInterpolatedStep
                ? to
                : Vector3.Lerp(from, to, alpha);

            var bounds = new Aabb(
                position - new Vector3(0.4f, 0f, 0.4f),
                position + new Vector3(0.4f, 1.9f, 0.4f));

            if (!_frustum.Intersects(bounds))
            {
                continue;
            }

            Vector3 travel = to - from;
            travel.Y = 0f;
            if (travel.LengthSquared > 1e-6f)
            {
                // Model míří osou +Z, stejně jako u hráče: theta = atan2(x, z).
                float cil = MathF.Atan2(travel.X, travel.Z);

                // OTÁČÍ SE POSTUPNĚ, NE SKOKEM. Cesta vede po mřížce, takže se směr mění po
                // pravých úhlech — a natočení nastavené natvrdo z toho udělá cukání jak na
                // šachovnici. Dotáčení přes nejkratší oblouk je jediné místo, kde se ta
                // hranatost dá schovat, aniž by se sahalo na hledání cesty.
                float rozdil = cil - _colonistYaw[id];
                while (rozdil > MathF.PI) { rozdil -= MathF.Tau; }
                while (rozdil < -MathF.PI) { rozdil += MathF.Tau; }

                float krok = ColonistTurnRate * (float)_frameTimer.DeltaSeconds;
                _colonistYaw[id] += Math.Clamp(rozdil, -krok, krok);
            }

            // ANIMACE ZE STAVU, NE Z ROZDÍLU BUNĚK. Předchozí buňka zůstane jiná i po tom,
            // co kolonista došel — podle ní by šlapal na místě donekonečna. Stav ví, jestli
            // se jde, kope, nebo se stojí u stroje.
            B3dAnimationClip clip = people.StateOf(id) switch
            {
                ColonistState.Moving or ColonistState.Delivering or ColonistState.Wandering
                    => B3dAnimationProfile.Character.Walk,
                ColonistState.Digging => B3dAnimationProfile.Character.Graze,
                _ => B3dAnimationProfile.Character.Idle,
            };

            // Fáze posunutá o index, ať nešlape celá parta na stejnou nohu.
            float span = MathF.Max(1f, clip.End - clip.Start);
            float frame = clip.Start
                + (((_playerAnimationTime * clip.Speed) + (id * 7.3f)) % span);

            _colonistPoses.Add((position, _colonistYaw[id], frame));
        }

        _chunkRenderer.Colonists = _colonistPoses;

        // NEJVÍC ZA CELÝ BĚH, ne stav na konci. Selftest odlétá od startu 45 m/s a kolonisté
        // zůstávají, kde vznikli — na konci běhu jich v záběru není žádný a nula by vypadala
        // jako chyba. Tohle je stejná past jako u ostatních měření: hodnota, která může být
        // nulová i u funkční věci, nic nedokazuje.
        _colonistPosesPeak = Math.Max(_colonistPosesPeak, _colonistPoses.Count);
    }


    /// <summary>Vrstvy pro pás, stroj, vkládač a item na pásu. Vážou se na JMÉNO textury.</summary>
    /// <remarks>
    /// Byla to čtyři natvrdo psaná čísla (5, 7, 9, 11) do atlasu tříděného podle jména.
    /// Vycházelo to na <c>acacia_planks</c>, <c>ancient_tech_panel</c>, <c>basalt</c>
    /// a <c>birch_leaves_02</c> — to poslední má 2 612 ze 4 096 texelů pod prahem výřezu,
    /// takže z itemu na pásu nebylo z dálky skoro nic vidět. Nepřihlášená vrstva zůstane
    /// na −1 a s ní se nekreslí nic. Důvody drží <see cref="ColonyTextureLayers"/>.
    /// </remarks>
    private ColonyTextureLayers _colonyLayers;

    /// <summary>
    /// Vykreslí, co kolonie postavila, a hlavně itemy jedoucí po pásech.
    /// </summary>
    /// <remarks>
    /// <para><b>Všechno jde přes jeden instancovaný draw call.</b> Pás, stroj, vkládač
    /// i každý item na pásu je jedna instance — geometrie je pořád tatáž kostka. Dvacet
    /// tisíc itemů se tím kreslí za stejný počet draw callů jako jeden.</para>
    ///
    /// <para><b>Polohu itemů zná jen vykreslování.</b> Simulace pracuje s odstupy a absolutní
    /// polohu nikdy nepotřebuje; tady se dopočítá z <c>WritePositions</c>, což je jediné
    /// místo, kde se platí za počet itemů — a to je cesta, která stejně musí projít každý
    /// viditelný kus.</para>
    /// </remarks>
    private void DrawColonyInstances()
    {
        if (_chunkRenderer is null)
        {
            return;
        }

        // Stroje a vkládače: pevné kostky na svých buňkách. Nepřihlášená vrstva značí −1,
        // se kterou se nekreslí nic — chybějící stroj se hlásí sám, špatně obarvený ne.
        if (_colonyLayers.Machine >= 0f)
        {
            for (int machine = 0; machine < _colony.Machines.Count; machine++)
            {
                if (_colony.Machines.IsRemoved(machine))
                {
                    continue;
                }

                Vector3i cell = _colony.Machines.CellOf(machine);
                _chunkRenderer.AddInstancedItem(
                    _frustum,
                    new Vector3(cell.X + 0.5f, cell.Y + 0.5f, cell.Z + 0.5f),
                    0.9f,
                    _colonyLayers.Machine,
                    _colony.Machines.IsPowered(machine) ? 1f : 0.45f);
            }
        }

        if (_colonyLayers.Inserter >= 0f)
        {
            for (int inserter = 0; inserter < _colony.Inserters.Count; inserter++)
            {
                if (_colony.Inserters.IsRemoved(inserter))
                {
                    continue;
                }

                Vector3i cell = _colony.Inserters.CellOf(inserter);
                _chunkRenderer.AddInstancedItem(
                    _frustum,
                    new Vector3(cell.X + 0.5f, cell.Y + 0.9f, cell.Z + 0.5f),
                    0.45f,
                    _colonyLayers.Inserter,
                    1f);
            }
        }

        // Pásy a itemy na nich. Bez přihlášených vrstev není co kreslit.
        Span<int> positions = stackalloc int[256];

        bool drawBelt = _colonyLayers.Belt >= 0f;
        bool drawBeltItem = _colonyLayers.BeltItem >= 0f;
        if (!drawBelt && !drawBeltItem)
        {
            return;
        }

        foreach (ColonyRuntime.BeltPlacement placement in _colony.Placements)
        {
            var direction = new Vector3(placement.Direction.X, placement.Direction.Y, placement.Direction.Z);
            var start = new Vector3(placement.Start.X + 0.5f, placement.Start.Y + 0.25f, placement.Start.Z + 0.5f);

            // Těleso pásu: kostka na každou buňku jeho délky.
            if (drawBelt)
            {
                for (int cell = 0; cell < placement.Belt.Cells; cell++)
                {
                    _chunkRenderer.AddInstancedItem(
                        _frustum, start + (direction * cell), 0.85f, _colonyLayers.Belt, 1f);
                }
            }

            if (!drawBeltItem)
            {
                continue;
            }

            // Itemy. Poloha je vzdálenost od VÝSTUPU, takže se odečítá od začátku pásu.
            int written = placement.Belt.WritePositions(positions);
            for (int i = 0; i < written; i++)
            {
                float along = positions[i] / (float)BeltSegment.StepsPerCell;
                _chunkRenderer.AddInstancedItem(
                    _frustum,
                    start + (direction * along) + new Vector3(0f, 0.45f, 0f),
                    0.3f,
                    _colonyLayers.BeltItem,
                    1f);
            }
        }
    }

    /// <summary>
    /// Kolik lidí kolonie dostane, jakmile je kde stát.
    /// </summary>
    /// <remarks>
    /// Přes <c>VOXELITY_COLONISTS</c> se dá zvednout na dvě stě, což je cíl ze sekce 8 —
    /// jinak se ten bod přejímky nedá změřit v běžící hře.
    /// </remarks>
    private readonly int _startingColonists =
        int.TryParse(
            Environment.GetEnvironmentVariable("VOXELITY_COLONISTS"),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out int requested) && requested > 0
            ? Math.Min(requested, 256)
            : 6;

    /// <summary>Označit při startu kus terénu k vykopání? Jen pro ověření celého řetězu.</summary>
    private readonly bool _colonyProbe =
        Environment.GetEnvironmentVariable("VOXELITY_COLONY_TEST") == "1";

    private bool _colonyProbeMarked;

    /// <summary>Postavit a zautomatizovat linku? Jen pro ověření scénáře přejímky v selftestu.</summary>
    private readonly bool _factoryProbe =
        Environment.GetEnvironmentVariable("VOXELITY_FACTORY_TEST") == "1";

    /// <summary>Postupné sypání rudy na pás. Viz TryExecuteFactoryCommand.</summary>
    private BeltSegment? _factoryFeedBelt;

    private ushort _factoryFeedItem;
    private int _factoryFeedRemaining;

    private int _factoryProbeStage;
    private int _factoryProbeTimer;

    /// <summary>Vstupní pás linky. Sonda ho na konci zbourá, aby se bourání ověřilo v enginu.</summary>
    private Vector3i _factoryProbeCell;

    /// <summary>Výstupní pás. Na něm itemy jsou, takže se na něm ukáže i návrat materiálu.</summary>
    private Vector3i _factoryProbeOutputCell;

    /// <summary>
    /// Projede scénář přejímky: postaví ruční drtič s člověkem, po chvíli napojí pásy
    /// a proud a vypíše, kolik lidí to uvolnilo.
    /// </summary>
    /// <remarks>
    /// Ve dvou krocích schválně. Kritérium T5 není „stroj funguje", ale „počítadlo volných
    /// lidí se zvedne v okamžiku napojení" — a to se dá ukázat jen tak, že se nejdřív ukáže
    /// stav bez automatizace.
    /// </remarks>
    private void TryRunFactoryProbe()
    {
        if (!_factoryProbe || _world is null || _colony.Colonists.Count == 0)
        {
            return;
        }

        _factoryProbeTimer++;
        FeedFactoryBelt();

        if (_factoryProbeStage == 0 && _factoryProbeTimer > 30)
        {
            TryExecuteFactoryCommand("/factory");
            Log.Info($"SONDA 1/4 rucne: volnych {_colony.FreeColonists} z {_colony.Colonists.Count}, "
                + $"strojů {_colony.MachineCount}, pasu {_colony.BeltCount}, "
                + $"itemu na pasech {_colony.ItemsOnBelts}");
            _factoryProbeStage = 1;
            _factoryProbeTimer = 0;
        }
        else if (_factoryProbeStage == 1 && _factoryProbeTimer > 60)
        {
            TryExecutePowerCommand("/power");
            _factoryProbeStage = 2;
            _factoryProbeTimer = 0;
        }
        else if (_factoryProbeStage == 2 && _factoryProbeTimer > 300)
        {
            Log.Info($"SONDA 2/4 automaticky: uvolnila automatizace {_colony.FreedByAutomation}, "
                + $"zdrceno {_colony.Machines.CraftedTotal}, "
                + $"vkladace prendaly {_colony.Inserters.MovedTotal}, "
                + $"itemu na pasech {_colony.ItemsOnBelts}");
            _factoryProbeStage = 3;
            _factoryProbeTimer = 0;
        }
        else if (_factoryProbeStage == 3 && _factoryProbeTimer > 60)
        {
            // BOURÁNÍ V BĚŽÍCÍM ENGINU. Zelený test není důkaz — tady se ukáže, že se stroj
            // vrátí do ručního režimu, vkládač zmizí a materiál z pásu skončí na skladu.
            int storedBefore = _colony.Colonists.StoredItems;
            int machine = FindMachineNear(_factoryProbeCell);
            MachineMode modeBefore = machine >= 0 ? _colony.Machines.ModeOf(machine) : MachineMode.Manual;

            ColonyRuntime.Demolished what = _colony.Demolish(_factoryProbeCell);
            MachineMode modeAfter = machine >= 0 ? _colony.Machines.ModeOf(machine) : MachineMode.Manual;

            Log.Info($"SONDA 3/4 bourani vkladace: zbourano {what} na {_factoryProbeCell}, "
                + $"rezim {modeBefore} -> {modeAfter}, "
                + $"na sklad slo {_colony.Colonists.StoredItems - storedBefore}, "
                + $"pasu {_colony.BeltCount}, vkladacu {_colony.InserterCount}, "
                + $"stroju {_colony.MachineCount}, volnych {_colony.FreeColonists}");
            _factoryProbeStage = 4;
            _factoryProbeTimer = 0;
        }
        else if (_factoryProbeStage == 4 && _factoryProbeTimer > 60)
        {
            // VÝSTUPNÍ pás, ne vstupní: na něm leží hotové kusy, takže jedna řádka ukáže obojí
            // najednou — stroj přijde o automatický režim a materiál z pásu skončí na skladu.
            int storedBefore = _colony.Colonists.StoredItems;
            int onBeltBefore = _colony.ItemsOnBelts;
            int machine = FindMachineNear(_factoryProbeOutputCell);
            MachineMode modeBefore = machine >= 0 ? _colony.Machines.ModeOf(machine) : MachineMode.Manual;

            // KLIKÁ SE, DOKUD NEPADNE PÁS. Na jedné buňce stojí vkládač i pás pod ním a vkládač
            // má přednost — což hráč dělá stejně, jen rukou.
            ColonyRuntime.Demolished what = ColonyRuntime.Demolished.Nothing;
            for (int click = 0; click < 3 && what != ColonyRuntime.Demolished.Belt; click++)
            {
                what = _colony.Demolish(_factoryProbeOutputCell);
                if (what == ColonyRuntime.Demolished.Nothing)
                {
                    break;
                }
            }

            MachineMode modeAfter = machine >= 0 ? _colony.Machines.ModeOf(machine) : MachineMode.Manual;

            Log.Info($"SONDA 4/4 bourani pasu: nakonec zbourano {what} na {_factoryProbeOutputCell}, "
                + $"rezim {modeBefore} -> {modeAfter}, na pasech bylo {onBeltBefore}, "
                + $"na sklad slo {_colony.Colonists.StoredItems - storedBefore}, "
                + $"pasu {_colony.BeltCount}, vkladacu {_colony.InserterCount}, "
                + $"stroju {_colony.MachineCount}, volnych {_colony.FreeColonists}");
            _factoryProbeStage = 5;
        }
    }

    /// <summary>
    /// Nasype každému načtenému stroji surovinu na jeho vstupní pás.
    /// </summary>
    /// <remarks>
    /// Bez toho je ověření bezcenné: linka bez suroviny stojí právem a z „nic se nestalo"
    /// nejde poznat, jestli se špatně načetla, nebo prostě nemá co dělat.
    /// </remarks>
    private int FeedLoadedMachines()
    {
        int fed = 0;

        for (int machine = 0; machine < _colony.Machines.Count; machine++)
        {
            if (_colony.Machines.IsRemoved(machine)
                || _colony.Machines.InputBeltOf(machine) is not { } belt)
            {
                continue;
            }

            if (_colony.TryPushOntoBelt(belt, _colony.Machines.InputItemOf(machine)))
            {
                fed++;
            }
        }

        return fed;
    }

    /// <summary>Ohlásí, co načtená linka stihla. Důkaz, že se nenačetl jen soubor.</summary>
    private void WatchLoadedColony()
    {
        if (_loadedColonyWatch <= 0 || --_loadedColonyWatch > 0)
        {
            return;
        }

        Log.Info($"Nactena linka po 300 ticich: nasypano {_loadedColonyFed}, "
            + $"zdrceno {_colony.Machines.CraftedTotal}, "
            + $"vkladace prendaly {_colony.Inserters.MovedTotal}, "
            + $"itemu na pasech {_colony.ItemsOnBelts}, "
            + $"na skladu {_colony.Colonists.StoredItems}, volnych {_colony.FreeColonists}");
    }

    /// <summary>Který stroj sousedí s buňkou. Pro sondu bourání.</summary>
    private int FindMachineNear(Vector3i cell)
    {
        for (int machine = 0; machine < _colony.Machines.Count; machine++)
        {
            if (_colony.Machines.IsRemoved(machine))
            {
                continue;
            }

            Vector3i delta = _colony.Machines.CellOf(machine) - cell;
            if (Math.Abs(delta.X) + Math.Abs(delta.Y) + Math.Abs(delta.Z) == 1)
            {
                return machine;
            }
        }

        return -1;
    }

    /// <summary>Sype rudu na pás po jednom kusu, s ohledem na rozestup.</summary>
    private void FeedFactoryBelt()
    {
        if (_factoryFeedRemaining <= 0 || _factoryFeedBelt is null)
        {
            return;
        }

        if (_colony.TryPushOntoBelt(_factoryFeedBelt, _factoryFeedItem))
        {
            _factoryFeedRemaining--;
        }
    }

    /// <summary>Vstoupit v selftestu do režimu velitele? Kvůli kolmému pohledu dolů.</summary>
    private readonly bool _commanderProbe =
        Environment.GetEnvironmentVariable("VOXELITY_COMMANDER_TEST") == "1";

    /// <summary>Kreslit panely i mimo režim velitele? Jen pro ověření v selftestu.</summary>
    private readonly bool _uiProbe = Environment.GetEnvironmentVariable("VOXELITY_UI_TEST") == "1";

    /// <summary>
    /// Označí kus terénu pod hráčem, aby šel celý řetěz ověřit i v selftestu.
    /// </summary>
    /// <remarks>
    /// Selftest neumí myš ani příkazy, ale scénář z přejímky M0 stojí na tom, že se označí
    /// oblast a kolonisté ji vykopou. Tohle je nejmenší způsob, jak to ověřit v běžící hře.
    /// </remarks>
    private void TryMarkProbeArea()
    {
        if (!_colonyProbe || _colonyProbeMarked || _world is null || _colony.Colonists.Count == 0)
        {
            return;
        }

        Vector3i feet = _colony.Colonists.CellOf(0);
        var minimum = new Vector3i(feet.X - 2, feet.Y - 1, feet.Z - 2);
        var maximum = new Vector3i(feet.X + 2, feet.Y - 1, feet.Z + 2);

        if (_colony.MarkArea(_world, _world.Registry, minimum, maximum) > 0)
        {
            _colonyProbeMarked = true;
            Log.Info($"Sonda kolonie: oznaceno {_colony.Jobs.OpenCount} ukolu.");
        }
    }

    /// <summary>
    /// Privita dalsiho kolonistu, kdyz na nej prisel cas.
    /// </summary>
    /// <remarks>
    /// <para><b>Bez radnice neprijde nikdo.</b> Driv se tady postavilo sest lidi kolem hrace,
    /// jakmile dojela navigace. Kolonie tim nemela stred ani zacatek a hrac nemel co
    /// rozhodnout. Ted lidi prichazeji az k radnici, kterou postavil.</para>
    ///
    /// <para>Kdo rozhoduje, jestli je cas, je <see cref="TownHall"/>; kdo vi, jestli je kam
    /// stoupnout, je navigace. Okno jen spoji obe odpovedi a zapise to do logu.</para>
    /// </remarks>
    private void TryWelcomeColonist()
    {
        // SONDA: zalozi kolonii pod hracem, aby sel colony sim overit v bezici hre.
        // Selftest neumi mys, takze bez tohohle by radnice nikdy nevznikla.
        if (_townHallProbe && !_colony.TownHall.IsFounded && _colony.NavigationChunks > 0)
        {
            // Hrac v selftestu leti vzduchem, takze se radnice musi posadit na zem.
            // Bez toho vznikne kolonie ve vzduchu a nikdo k ni nedojde.
            var feet = new Vector3i(
                (int)MathF.Floor(_player.Position.X),
                (int)MathF.Floor(_player.Position.Y),
                (int)MathF.Floor(_player.Position.Z));

            for (int drop = 0; drop < 64; drop++)
            {
                var candidate = new Vector3i(feet.X, feet.Y - drop, feet.Z);
                if (_colony.Navigation.IsStandable(candidate))
                {
                    if (_colony.FoundTownHall(candidate))
                    {
                        Log.Info($"SONDA RADNICE: kolonie zalozena na {candidate}, "
                            + $"sklad na {_colony.Store.Cell}.");
                    }

                    break;
                }
            }
        }

        if (_colony.NavigationChunks == 0)
        {
            return;
        }

        int colonist = _colony.TryWelcomeColonist();
        if (colonist >= 0)
        {
            Log.Info($"Prisel kolonista {colonist}, kolonie ma {_colony.Colonists.Count} lidi.");
        }
    }

    /// <summary>Postavit radnici sondou? Selftest neumí myš, jinak by kolonie nevznikla.</summary>
    private readonly bool _townHallProbe =
        Environment.GetEnvironmentVariable("VOXELITY_TOWNHALL_TEST") == "1";

    /// <summary>Přihlásilo se už kolonii jídlo? Váže se na jméno, ne na číslo.</summary>
    private bool _colonyFoodResolved;

    /// <summary>
    /// Řekne kolonii, co je jídlo. Jednou za běh, jakmile je registr bloků k dispozici.
    /// </summary>
    /// <remarks>
    /// <b>Jména, nikdy natvrdo psaná čísla</b>. Co obsah nezná, se vypíše
    /// do logu — jinak by se chybějící jídlo poznalo až tím, že kolonisté hladoví a nikam
    /// nejdou, což vypadá jako chyba pathfindingu.
    /// </remarks>
    private void ResolveColonyFoodOnce()
    {
        if (_colonyFoodResolved || _world is null)
        {
            return;
        }

        _colonyFoodResolved = true;
        _colony.ResolveFood(_world.Registry);

        IReadOnlyList<string> missing = ColonyFood.MissingNames(_world.Registry);
        Log.Info($"Kolonie zna {ColonyFood.Names.Count - missing.Count} druhu jidla"
            + (missing.Count > 0 ? $", chybi v obsahu: {string.Join(", ", missing)}." : "."));
    }

    /// <summary>Ověřit hlad v běžící hře? Bez toho se na ten scénář selftest nedostane.</summary>
    private readonly bool _hungerProbe =
        Environment.GetEnvironmentVariable("VOXELITY_HUNGER_TEST") == "1";

    /// <summary>Nechat sondu bez jídla? Kontrolní běh k tomu s jídlem.</summary>
    private readonly bool _hungerProbeNoFood =
        Environment.GetEnvironmentVariable("VOXELITY_HUNGER_NOFOOD") == "1";

    private int _hungerProbeStage;
    private int _hungerProbeTimer;
    private int _hungerProbeDugAtStart;
    private int _hungerProbeHungerAtStart;

    /// <summary>
    /// Rozhodovací sonda hladu: hladový kolonista dojde ke skladu, nají se a hlad klesne.
    /// </summary>
    /// <remarks>
    /// <para><b>Prázdná kolonie nedokazuje nic</b> (zadání). Sonda proto nejdřív označí kus
    /// terénu, aby lidé opravdu pracovali, pak jim nastaví hlad těsně pod práh a teprve potom
    /// se dívá, jestli se někdo najedl.</para>
    ///
    /// <para><b>Kontrolní běh je součástí sondy, ne dodatek.</b> S <c>VOXELITY_HUNGER_NOFOOD=1</c>
    /// se do skladu nic nedá; hlad pak musí růst dál a práce se zpomalit. Bez toho by se nedalo
    /// poznat, jestli měřím jídlo, nebo jen to, že čas plyne.</para>
    /// </remarks>
    private void TryRunHungerProbe()
    {
        if (!_hungerProbe || _world is null || _colony.Colonists.Count == 0)
        {
            return;
        }

        _hungerProbeTimer++;

        // KONTROLNÍ BĚH DRŽÍ HLAD NA MÍSTĚ. Bez toho by se měřil postupný přechod přes práh
        // a z čísel by nešlo poznat, jestli je vidět zpomalení, nebo jen jiný okamžik.
        if (_hungerProbeNoFood && _hungerProbeStage >= 2)
        {
            for (int id = 0; id < _colony.Colonists.Count; id++)
            {
                _colony.Colonists.SetHunger(id, ColonySimulation.StarvingAt);
            }
        }

        if (_hungerProbeStage == 0 && _hungerProbeTimer > 30)
        {
            // PRÁCE NEJDŘÍV. Bez ní stojí kolonisté právem a z „nic se neděje" nejde nic poznat.
            Vector3i feet = _colony.Colonists.CellOf(0);
            _colony.MarkArea(
                _world,
                _world.Registry,
                new Vector3i(feet.X - 3, feet.Y - 1, feet.Z - 3),
                new Vector3i(feet.X + 3, feet.Y - 1, feet.Z + 3));

            Log.Info($"SONDA HLADU 1/4: oznaceno {_colony.Jobs.OpenCount} ukolu, "
                + $"lidi {_colony.Colonists.Count}, sklad na "
                + $"{(_colony.Store.HasCell ? _colony.Store.Cell.ToString() : "NIKDE")}.");

            _hungerProbeStage = 1;
            _hungerProbeTimer = 0;
        }
        else if (_hungerProbeStage == 1 && _hungerProbeTimer > 120)
        {
            // Jídlo do skladu. Zdroj jídla neřeším (zadání) — sonda ho tam dá rovnou.
            int food = 0;
            if (!_hungerProbeNoFood)
            {
                ushort[] kinds = ColonyFood.Resolve(_world.Registry);
                if (kinds.Length > 0)
                {
                    _colony.Store.Add(kinds[0], 8);
                    food = 8;
                }
            }

            // S jídlem stačí těsně pod práh hladu: cílem je vidět, jak si člověk dojde jíst.
            //
            // BEZ JÍDLA ROVNOU NA PRÁH VYHLADOVĚNÍ. Dojít tam přirozeně trvá dvě minuty
            // herního času a selftest tolik nemá, takže by fáze 4 nikdy nedoběhla —
            // a kontrolní běh by se tvářil v pořádku jen proto, že se nespustil.
            for (int id = 0; id < _colony.Colonists.Count; id++)
            {
                _colony.Colonists.SetHunger(
                    id,
                    _hungerProbeNoFood
                        ? ColonySimulation.StarvingAt
                        : ColonySimulation.HungryAt - 30);
            }

            _hungerProbeDugAtStart = _colony.Jobs.DoneCount;
            _hungerProbeHungerAtStart = _colony.Colonists.WorstHunger;

            Log.Info($"SONDA HLADU 2/4: do skladu slo {food} jidla, "
                + $"sklad ma {_colony.Store.Total} kusu v {_colony.Store.KindCount} druzich, "
                + $"nejvyssi hlad {_hungerProbeHungerAtStart}, vykopano {_hungerProbeDugAtStart}.");

            _hungerProbeStage = 2;
            _hungerProbeTimer = 0;
        }
        else if (_hungerProbeStage == 2 && _hungerProbeTimer > 300)
        {
            // ROZHODOVACÍ ŘÁDKA. S jídlem musí hlad klesnout a MealsEaten vyskočit;
            // bez jídla musí hlad růst a nikdo se nenají.
            Log.Info($"SONDA HLADU 3/4 po 300 ticich: snedeno {_colony.Colonists.MealsEaten} jidel, "
                + $"nejvyssi hlad {_colony.Colonists.WorstHunger} (na zacatku "
                + $"{_hungerProbeHungerAtStart}), hladovi {_colony.Colonists.StarvingCount}, "
                + $"ve skladu jidla {_colony.Store.FoodCount}, "
                + $"vykopano {_colony.Jobs.DoneCount - _hungerProbeDugAtStart}.");

            _hungerProbeStage = 3;
            _hungerProbeTimer = 0;
            _hungerProbeDugAtStart = _colony.Jobs.DoneCount;
        }
        else if (_hungerProbeStage == 3 && _hungerProbeTimer > 900)
        {
            // ZPOMALENÍ PRÁCE. Za stejný počet tiků se bez jídla vykope míň — a je to
            // vidět na čísle, ne jen v kódu.
            Log.Info($"SONDA HLADU 4/4 za dalsich 900 ticku: vykopano "
                + $"{_colony.Jobs.DoneCount - _hungerProbeDugAtStart}, "
                + $"snedeno celkem {_colony.Colonists.MealsEaten}, "
                + $"nejvyssi hlad {_colony.Colonists.WorstHunger}, "
                + $"hladovi {_colony.Colonists.StarvingCount} z {_colony.Colonists.Count}, "
                + $"sklad {_colony.Store.Total} kusu ({_colony.Store.FoodCount} jidla).");

            _hungerProbeStage = 4;
        }
    }

    /// <summary>Vypnout cullování sondy? Jen pro měření nejhoršího případu.</summary>
    private readonly bool _probeSkipCulling =
        Environment.GetEnvironmentVariable("VOXELITY_ITEMS_NOCULL") == "1";

    private static int ParseProbeCount() =>
        int.TryParse(
            Environment.GetEnvironmentVariable("VOXELITY_ITEMS"),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out int value) && value > 0
            ? Math.Min(value, 200_000)
            : 0;

    /// <summary>Rozdělení času celého simulačního tiku. Cíl je p99 pod 8 ms (sekce 8).</summary>
    private FrameStats TickTotalStats() => _tickPhases.Stats(TickPhaseTotal);

    /// <summary>Rozdělení času simulačního tiku pro selftest report.</summary>
    public FrameStats TickStats => TickTotalStats();

    /// <summary>Stav kolonie pro selftest report.</summary>
    public string ColonyStatus => string.Create(
        System.Globalization.CultureInfo.InvariantCulture,
        $"lidi {_colony.Colonists.Count} (volnych {_colony.FreeColonists}), "
        + $"radnice {(_colony.TownHall.IsFounded ? _colony.TownHall.Cell.ToString() : "NENI")}"
        + $" (prislo {_colony.TownHall.Arrived}), "
        + $"navigace {_colony.NavigationChunks} chunku, "
        + $"prace {_colony.Jobs.OpenCount}/{_colony.Jobs.ClaimedCount}/{_colony.Jobs.DoneCount}, "
        + $"sklad {_colony.Store.Total} kusu/{_colony.Store.KindCount} druhu "
        + $"(jidla {_colony.Store.FoodCount}), "
        + $"hlad nejvyssi {_colony.Colonists.WorstHunger} hladovi "
        + $"{_colony.Colonists.StarvingCount} snedeno {_colony.Colonists.MealsEaten}, "
        + $"postav v zaberu {_colonistPoses.Count} (nejvic za beh {_colonistPosesPeak}), "
        + $"vrstva kuze {_chunkRenderer?.PlayerSkinLayer ?? -1f} (ceka se {_playerSkinLayer}), "
        + $"vrstvy kolonie pas {_colonyLayers.Belt} stroj {_colonyLayers.Machine} "
        + $"vkladac {_colonyLayers.Inserter} item {_colonyLayers.BeltItem}"
        + $"{(_colonyLayers.IsComplete ? "" : " CHYBI " + string.Join("+", _colonyLayers.MissingNames()))}");

    /// <summary>Kolik instancovaných itemů se kreslilo. Pro selftest report.</summary>
    public int InstancedItems => _chunkRenderer?.InstancedItemCount ?? 0;

    /// <summary>Kolik instancovaných itemů se zahodilo mimo záběr.</summary>
    public int InstancedItemsCulled => _chunkRenderer?.InstancedItemsCulled ?? 0;

    /// <summary>Kolik ms zabralo nahrání instancí na grafiku.</summary>
    public double InstancedItemsUploadMs => _chunkRenderer?.InstancedItemsUploadMs ?? 0.0;

    /// <summary>
    /// Rozpad tiku po kategoriích, pro selftest report. Vrací p50 i p99 v milisekundách.
    /// </summary>
    /// <remarks>
    /// <para><b>ROZPAD MUSÍ HLÁSIT TOTÉŽ, CO SE POROVNÁVÁ.</b> Vracelo se odsud p50, kdežto
    /// celý tik se posuzuje podle p99 — takže se vedle sebe psala dvě různá čísla a vypadalo
    /// to, že se čas ztrácí. Konkrétně: celek p99 18,9 ms proti součtu rozpadu 1,2 ms, tedy
    /// „sedmnáct milisekund nikde". Žádných sedmnáct milisekund nechybělo; jen se medián
    /// srovnával se špičkou.</para>
    ///
    /// <para>Stálo mě to dvě hodiny hledání a jedno tvrzení v commitu, které jsem musel vzít
    /// zpátky. <b>Špička se dá porovnat jen se špičkou</b> — p50 je proti p99 jiná veličina,
    /// ne jiný pohled na tutéž.</para>
    /// </remarks>
    public IEnumerable<(string Name, double P50Ms, double P99Ms)> TickBreakdown()
    {
        for (int i = 0; i < TickPhaseTotal; i++)
        {
            FrameStats stats = _tickPhases.Stats(i);
            yield return (_tickPhases.Name(i), stats.P50Ms, stats.P99Ms);
        }
    }

    /// <summary>
    /// Přepočítá diagnostiku děr — ale <b>jen když je vidět</b>, a i tak nejvýš dvakrát
    /// za vteřinu. Prochází celý dohled včetně sousedů kandidátů, takže na každý frame
    /// je to moc; při zavřeném menu je to práce úplně zbytečná.
    /// </summary>
    private void UpdateDiagnostics()
    {
        if (_streamer is null || !_menu.Visible)
        {
            return;
        }

        _diagnosticsAge += _frameTimer.DeltaSeconds;
        if (_diagnosticsAge < 0.5)
        {
            return;
        }

        _diagnosticsAge = 0.0;
        _streamer.RunDiagnostics();
    }

    /// <summary>
    /// Kroky podle uražené vzdálenosti, ne podle času — při chůzi i sprintu tak vychází
    /// stejný rozestup stop, jen rychleji za sebou.
    /// </summary>
    private void UpdateFootsteps()
    {
        if (_world is null || _sounds is null || _selftestFrames > 0)
        {
            return;
        }

        Vector3 position = _player.Position;
        float travelled = new Vector2(position.X - _previousFootPosition.X, position.Z - _previousFootPosition.Z).Length;
        _previousFootPosition = position;

        if (!_player.OnGround || _player.NoClip)
        {
            return;
        }

        _walkedSinceStep += travelled;
        if (_walkedSinceStep < StepDistance)
        {
            return;
        }

        _walkedSinceStep = 0f;

        // Materiál se bere z bloku pod chodidly.
        var under = new Vector3i(
            (int)MathF.Floor(position.X),
            (int)MathF.Floor(position.Y - 0.1f),
            (int)MathF.Floor(position.Z));

        if (TryGetMaterial(under, out BlockMaterial material))
        {
            _sounds.Play(material, BlockAction.Step, under, volume: 0.5f);
        }
    }

    /// <summary>
    /// Materiál bloku. U otesaného bloku je v blokové vrstvě vzduch, takže se materiál
    /// musí vzít z mikro dat.
    /// </summary>
    private bool TryGetMaterial(Vector3i block, out BlockMaterial material)
    {
        material = BlockMaterial.None;

        if (_world is null)
        {
            return false;
        }

        ushort id = _world.GetBlock(block.X, block.Y, block.Z);

        if (_world.Registry.IsAir(id))
        {
            MicroBlock? micro = _world.GetMicro(block.X, block.Y, block.Z);
            if (micro is null)
            {
                return false;
            }

            id = micro.DominantMaterial();
            if (_world.Registry.IsAir(id))
            {
                return false;
            }
        }

        material = _world.Registry.Definition(id).Material;
        return true;
    }

    private float SampleDropLight(Vector3 position)
    {
        if (_world is null || _registry is null) return 1f;

        var origin = new Vector3i(
            (int)MathF.Floor(position.X),
            (int)MathF.Floor(position.Y + 0.1f),
            (int)MathF.Floor(position.Z));
        long now = Stopwatch.GetTimestamp();
        if (_dropLightCache.TryGetValue(origin, out var cached)
            && (now - cached.Time) < Stopwatch.Frequency / 5)
        {
            return cached.Light;
        }

        const byte radius = 14;
        byte strongest = 0;
        _dropLightQueue.Clear();
        _dropLightVisited.Clear();
        _dropLightQueue.Enqueue((origin, 0));
        _dropLightVisited.Add(origin);

        while (_dropLightQueue.TryDequeue(out var node))
        {
            ushort here = _world.GetBlock(node.Position.X, node.Position.Y, node.Position.Z);
            if (_registry.IsOpaque(here)) continue;

            byte emission = here < _registry.EmissionTable.Length
                ? _registry.EmissionTable[here]
                : (byte)0;
            if (emission > node.Distance)
            {
                strongest = Math.Max(strongest, (byte)(emission - node.Distance));
            }

            if (node.Distance >= radius || strongest >= 15) continue;

            byte next = (byte)(node.Distance + 1);
            Enqueue(node.Position + Vector3i.UnitX, next);
            Enqueue(node.Position - Vector3i.UnitX, next);
            Enqueue(node.Position + Vector3i.UnitY, next);
            Enqueue(node.Position - Vector3i.UnitY, next);
            Enqueue(node.Position + Vector3i.UnitZ, next);
            Enqueue(node.Position - Vector3i.UnitZ, next);
        }

        // Tohle je pouze BLOKOVE svetlo, tedy nocni banka. Slunce a obloha se na predmet
        // aplikuji zvlast pres SampleDynamicSkyLight; otevreny sloupec proto nesmi
        // predstirat pochoden o sile 15. Krivka je tataz jako u terenu.
        float result = LuantiLight.Brightness(strongest);
        if (_dropLightCache.Count > 4096) _dropLightCache.Clear();
        _dropLightCache[origin] = (now, result);
        return result;

        void Enqueue(Vector3i candidate, byte distance)
        {
            if (candidate.Y < 0 || candidate.Y >= TerrainGenerator.WorldHeight
                || !_dropLightVisited.Add(candidate)) return;
            _dropLightQueue.Enqueue((candidate, distance));
        }

    }

    /// <summary>
    /// Denní (sluneční) banka pro věc, která není součástí terénu: ležící předmět, zvíře,
    /// postava hráče, držená věc.
    /// </summary>
    /// <remarks>
    /// <para><b>Čte se SKUTEČNÝ svět, ne generátor.</b> Dřív se tady porovnávala výška
    /// s <c>TerrainGenerator.SurfaceHeight</c>, tedy s povrchem, jaký by terén měl, kdyby
    /// do něj nikdo nesáhl. Předmět v hráčem vykopané jámě proto svítil naplno a předmět
    /// v hráčem postavené místnosti taky, zatímco cokoli ležícího na zemi občas spadlo do
    /// podzemní větve a bylo uhlově černé.</para>
    ///
    /// <para><b>Chování odpovídá Luanti:</b> volný sloupec k obloze je 15, jinak se
    /// hledá nejbližší volný sloupec a za každý krok se ubere jedna úroveň. Vchod do
    /// jeskyně proto plynule tmavne, místo aby na prahu skočil.</para>
    /// </remarks>
    private float SampleDynamicSkyLight(Vector3 position)
    {
        if (_world is null || _registry is null)
        {
            return 1f;
        }

        var origin = new Vector3i(
            (int)MathF.Floor(position.X),
            (int)MathF.Floor(position.Y + 0.1f),
            (int)MathF.Floor(position.Z));
        long now = Stopwatch.GetTimestamp();
        if (_skyLightCache.TryGetValue(origin, out var cached)
            && (now - cached.Time) < Stopwatch.Frequency / 5)
        {
            return cached.Light;
        }

        const byte radius = 12;
        byte strongest = 0;
        _skyLightQueue.Clear();
        _skyLightVisited.Clear();
        _skyLightQueue.Enqueue((origin, 0));
        _skyLightVisited.Add(origin);

        while (_skyLightQueue.TryDequeue(out var node))
        {
            if (_registry.IsOpaque(_world.GetBlock(node.Position.X, node.Position.Y, node.Position.Z)))
            {
                continue;
            }

            if (SeesSky(node.Position))
            {
                strongest = Math.Max(strongest, (byte)(LuantiLight.LightSun - node.Distance));
                if (strongest >= LuantiLight.LightSun)
                {
                    break;
                }
            }

            if (node.Distance >= radius)
            {
                continue;
            }

            byte next = (byte)(node.Distance + 1);
            Enqueue(node.Position + Vector3i.UnitX, next);
            Enqueue(node.Position - Vector3i.UnitX, next);
            Enqueue(node.Position + Vector3i.UnitY, next);
            Enqueue(node.Position - Vector3i.UnitY, next);
            Enqueue(node.Position + Vector3i.UnitZ, next);
            Enqueue(node.Position - Vector3i.UnitZ, next);
        }

        // Táž převodní křivka jako u terénu. Kdyby se lišila, měl by předmět ležící na
        // trávě jiný jas než tráva pod ním.
        float result = LuantiLight.Brightness(strongest);
        if (_skyLightCache.Count > 4096) _skyLightCache.Clear();
        _skyLightCache[origin] = (now, result);
        return result;

        void Enqueue(Vector3i candidate, byte distance)
        {
            if (candidate.Y < 0 || candidate.Y >= TerrainGenerator.WorldHeight
                || !_skyLightVisited.Add(candidate)) return;
            _skyLightQueue.Enqueue((candidate, distance));
        }
    }

    /// <summary>
    /// Vidí tenhle voxel přímo na oblohu, tedy je nad ním jen průhledno?
    /// </summary>
    /// <remarks>
    /// Sloupec se prochází jen do pevného stropu nad hlavou, ne až k <c>WorldHeight</c>.
    /// Přes celý sloupec by to na každý dotaz stálo stovky čtení a volá se to pro každý
    /// předmět a každé zvíře v každém snímku; nad hlavou hráče přitom nic tak vysokého
    /// nestojí, aby na tom rozdíl byl vidět.
    /// </remarks>
    private bool SeesSky(Vector3i position)
    {
        const int ceiling = 48;
        int top = Math.Min(TerrainGenerator.WorldHeight - 1, position.Y + ceiling);

        for (int y = position.Y + 1; y <= top; y++)
        {
            // Tentýž předpoklad jako u terénu: slunce zastaví i listí, ne jen kámen.
            if (_registry!.BlocksSunlight(_world!.GetBlock(position.X, y, position.Z)))
            {
                return false;
            }
        }

        return true;
    }

    protected override void OnRenderFrame(FrameEventArgs args)
    {
        base.OnRenderFrame(args);

        if (_renderer is null)
        {
            return;
        }

        // Zminimalizované okno má nulovou plochu a kreslit do něj nemá smysl.
        if ((int)_swapchain!.Extent.Width == 0 || (int)_swapchain!.Extent.Height == 0)
        {
            return;
        }

        // BeginFrame čeká na fenci předchozího snímku, tedy na GPU. Ten čas se musí měřit
        // zvlášť — jinak by se počítal do „času hlavního vlákna" a scéna by vypadala jako
        // CPU-bound i ve chvíli, kdy hlavní vlákno jen stojí a čeká na grafiku.
        _phases.Begin(PhaseWaitGpu);
        bool acquired = _renderer.BeginFrame();
        _phases.End(PhaseWaitGpu);

        if (!acquired)
        {
            // Swapchain je zastaralý; snímek se zahodí a příště se předělá.
            return;
        }

        // Poměr stran se bere z rozměrů swapchainu, ne z ClientSize. Na displeji s vyšším
        // rozlišením (Retina) je swapchain v pixelech větší než okno v bodech, a projekce
        // musí sedět na to, do čeho se opravdu kreslí.
        _modRenderBridge?.BeginFrame();

        _camera.AspectRatio = (int)_swapchain!.Extent.Height == 0
            ? 1f
            : (int)_swapchain!.Extent.Width / (float)(int)_swapchain!.Extent.Height;

        Matrix4 viewProjection = _camera.ViewMatrix * _camera.ProjectionMatrix;
        Tesseris.ModApi.ModRenderViewSnapshot modRenderView = GameModRenderBridge.CreateViewSnapshot(
            _camera,
            (int)_swapchain!.Extent.Width,
            (int)_swapchain!.Extent.Height,
            (float)(_mods?.SimulationClock.InterpolationAlpha ?? 0d));

        // OSY PAPRSKU PRO OBLOHU. Skládají se z kamery, ne z inverze její matice — proč,
        // je u ChunkRenderer.SkyRay. Rozevření se do nich zapéká rovnou, aby shader
        // nemusel počítat nic než součet tří vektorů.
        if (_chunkRenderer is not null)
        {
            float tanHalf = MathF.Tan(MathHelper.DegreesToRadians(_camera.FieldOfViewDegrees) * 0.5f);

            // POHLEDOVÁ BÁZE, NE MÍŘENÍ. V pohledu zepředu je kamera otočená proti směru, kam
            // hráč míří — kdyby se obloha počítala z `Forward`, otáčela by se proti světu.
            _chunkRenderer.SkyRay = (
                _camera.ViewRight * tanHalf * _camera.AspectRatio,

                // MÍNUS PATŘÍ KE KOREKČNÍ MATICI. VulkanClip.ToVulkan obrací osu Y, takže
                // v NDC míří dolů, kdežto Up kamery nahoru — bez mínusu je obloha vzhůru
                // nohama: při pohledu na obzor je vidět spodní polovina klenby, tedy bez
                // mraků i slunce, a obojí se objeví teprve při pohledu kolmo vzhůru.
                //
                // Pod OpenGL, kde korekce zmizela, tu mínus naopak být NESMÍ. Ty dvě věci
                // se musí měnit spolu; každá zvlášť dá tentýž rozbitý obraz.
                -_camera.ViewUp * tanHalf,
                _camera.ViewForward);
        }
        // RUKA MÁ VLASTNÍ ZORNÝ ÚHEL. Pevných 70° znamená, že se nesený předmět nehne, ať si
        // hráč nastaví dohled jakkoli — jinak se při širokém FOV odsune do rohu a protáhne.
        if (_chunkRenderer is not null)
        {
            const float HeldFieldOfView = 70f;
            _chunkRenderer.HeldViewProjection = (
                _camera.ViewMatrix
                * Matrix4.CreatePerspectiveFieldOfView(
                    MathHelper.DegreesToRadians(HeldFieldOfView),
                    _camera.AspectRatio,
                    _camera.NearPlane,
                    _camera.FarPlane));
        }

        _frustum.Update(viewProjection);

        long drawStart = Stopwatch.GetTimestamp();
        _phases.Begin(PhaseDraw);
        // i barva světla musí v rámci snímku vidět tutéž denní dobu.
        if (_chunkRenderer is not null)
        {
            // DENNÍ ČAS SE POSOUVÁ V SIMULAČNÍM TIKU, ne tady. Dřív se volalo Day.Update
            // s délkou snímku, takže na výkonnějším stroji utíkal herní den rychleji.
            // Zůstává tu jen odvození barev, které z denní doby jen čte.

            // jaký je za ním. Bez toho by při západu zůstala modrá i v oranžové obloze.
            Vector3 horizon = _chunkRenderer.Day.Horizon();
            _chunkRenderer.FogColor = horizon;
        }

        _renderer.BeginScene();
        _modRenderBridge?.DrawPhase(
            Tesseris.ModApi.ModRenderPhase.BeforeWorld,
            modRenderView,
            viewProjection,
            _camera.Right,
            _camera.Up,
            _stats);

        if (_chunkRenderer is not null)
        {
            RefreshPowerMesh();
            _chunkRenderer.Drops = _drops?.All;
            _chunkRenderer.DropItems = _items;
            _chunkRenderer.DropLightSampler = _world is null ? null : SampleDropLight;
            _chunkRenderer.SkyLightSampler = _world is null ? null : SampleDynamicSkyLight;
            _chunkRenderer.Animals = _animals?.Animals;
            _chunkRenderer.CrackLayer = _crackLayer;

            // Praskliny jen když se opravdu kope. Bez cíle se nekreslí nic.
            _chunkRenderer.Breaking = _mining?.Target is { } target && _mining.Progress > 0f
                && BreakingBounds(target) is { } bounds
                    ? (target, _mining.Progress, bounds)
                    : null;
        }

        FillInstancedItemProbe();
        FillColonistPoses();

        _chunkRenderer?.Draw(
            viewProjection, _camera.Position, _camera.NearPlane, _camera.FarPlane,
            _frustum, _stats);

        _modRenderBridge?.DrawPhase(
            Tesseris.ModApi.ModRenderPhase.OpaqueWorld,
            modRenderView,
            viewProjection,
            _camera.Right,
            _camera.Up,
            _stats);
        _modRenderBridge?.DrawPhase(
            Tesseris.ModApi.ModRenderPhase.TransparentWorld,
            modRenderView,
            viewProjection,
            _camera.Right,
            _camera.Up,
            _stats);

        // ZÁŘE AŽ NAD HOTOVOU SCÉNOU, včetně vody — ta se má rozzářit taky. Potřebuje
        // čerstvou kopii obrazu: ta, kterou si pořídila voda, je z doby před vodou.
        if (_chunkRenderer is { Bloom: true })
        {
            _renderer.CaptureScene(once: false);
            _chunkRenderer.DrawBloom();
        }

        // Obrys se kreslí až za září, aby ho nerozmazala.
        DrawAimOutline(viewProjection);
        _modRenderBridge?.DrawPhase(
            Tesseris.ModApi.ModRenderPhase.AfterWorld,
            modRenderView,
            viewProjection,
            _camera.Right,
            _camera.Up,
            _stats);
        _phases.End(PhaseDraw);

        if (_selftestFrames > 0 && _frameTimer.FrameCount > 100)
        {
            double drawMs = (Stopwatch.GetTimestamp() - drawStart) * 1000.0 / Stopwatch.Frequency;
            WorstDrawMs = Math.Max(WorstDrawMs, drawMs);
        }

        _phases.Begin(PhaseOverlay);
        if (!_pauseMenuOpen)
        {
            _modRenderBridge?.DrawPhase(
                Tesseris.ModApi.ModRenderPhase.Hud,
                modRenderView,
                viewProjection,
                _camera.Right,
                _camera.Up,
                _stats);
        }
        _renderer.BeginPresentation();
        _chunkRenderer?.DrawColorGrade();

        if (_selftestFrames == 0 && _startupStage != StartupStage.Playing)
        {
            DrawStartupScreen();
        }
        else if (_overlayVisible && !_pauseMenuOpen)
        {
            DrawOverlay();
        }
        else if (_startupStage == StartupStage.Playing)
        {
            DrawModHud();
        }

        if (_startupStage == StartupStage.Playing && !_pauseMenuOpen && HasActiveModScreen)
        {
            DrawModScreen();
        }

        if (_startupStage == StartupStage.Playing && !_pauseMenuOpen
            && (_modCommandOpen || _modCommandMessageSeconds > 0f))
        {
            DrawModCommandPrompt();
        }

        if (_startupStage == StartupStage.Playing && _pauseMenuOpen)
        {
            DrawPauseMenu();
        }

        _phases.End(PhaseOverlay);

        // Žádost o přečtení obrazovky přijde ještě před prohozením bufferů: čte se ze
        // zadního, tedy z toho, do kterého se právě kreslilo.
        RequestSelftestCapture();
        RequestFlickerCapture();

        _frameTimer.EndCpuWork();

        _phases.Begin(PhasePresent);
        _modRenderBridge?.EndFrame();
        _renderer.EndFrame();

        // Prohození bufferů se tu NEVOLÁ. Ve Vulkanu obraz odesílá swapchain uvnitř
        // EndFrame; okno bez grafického kontextu SwapBuffers ani neumí a skončilo by
        // výjimkou „Cannot use SwapBuffers when running with ContextAPI.NoAPI".
        _phases.End(PhasePresent);

        ConsumeSelftestCapture();
        ConsumeFlickerCapture();
        _phases.EndFrame();

        ReportHitch(args.Time * 1000.0);

        // PRŮBĚH SPOTŘEBY GRAFICKÉ PAMĚTI. Jedno číslo nic neřekne — teprve řada ukáže,
        // jestli spotřeba konverguje k pracovní sadě, nebo leze pořád nahoru, což by
        // znamenalo, že se místo po odstraněných chuncích nevrací.
        if (_selftestFrames > 0 && _frameTimer.FrameCount == _selftestFrames - 5
            && _chunkRenderer?.GpuProfiler is { Available: true } gpu)
        {
            var line = new System.Text.StringBuilder("GPU po pruchodech:");
            for (int section = 0; section < gpu.SectionCount; section++)
            {
                line.Append(CultureInfo.InvariantCulture, $" {gpu.Name(section)}={gpu.Milliseconds(section):F2}");
            }

            Log.Info(line.Append(CultureInfo.InvariantCulture, $" | celkem {gpu.Total:F2} ms").ToString());
        }
    }

    /// <summary>
    /// Kolik milisekund smí snímek trvat, než se považuje za zásek.
    /// </summary>
    /// <remarks>
    /// Osm milisekund je 125 snímků za vteřinu. Scéna jinak jede přes šest set, takže
    /// všechno nad tímhle je opravdu cuknutí, ne kolísání.
    /// </remarks>
    private const double HitchBudgetMs = 8.0;

    /// <summary>Kolik záseků se ještě smí vypsat. Bez stropu by log zahltil běh.</summary>
    private int _hitchesLeft = 40;

    /// <summary>
    /// Počty úklidů paměti na začátku snímku. Rozdíl proti konci říká, jestli se v tomhle
    /// snímku uklízelo — a hlavně které generace.
    /// </summary>
    /// <remarks>
    /// <para><b>Proč se to sleduje.</b> První běh logu ukázal záseky 18 až 39 ms, ve kterých
    /// <b>žádná měřená fáze nepřesáhla desetinu milisekundy</b>. Čas se tedy ztrácel mimo
    /// veškerou práci hlavního vlákna, a to umí jediná věc: úklid paměti, který zastaví
    /// všechna vlákna naráz — i ta, co zrovna nic nealokují.</para>
    ///
    /// <para>Generace 2 je nejdražší, protože prochází celou haldu. Generace 0 bývá pod
    /// milisekundu a v záseku téhle velikosti nefiguruje.</para>
    /// </remarks>
    private readonly int[] _gcAtFrameStart = new int[3];

    private void SnapshotGcCounts()
    {
        _gcAtFrameStart[0] = GC.CollectionCount(0);
        _gcAtFrameStart[1] = GC.CollectionCount(1);
        _gcAtFrameStart[2] = GC.CollectionCount(2);
    }

    /// <summary>
    /// Vypíše rozpad fází, když snímek přeteče rozpočet.
    /// </summary>
    /// <remarks>
    /// <para><b>Proč to logovat a ne jen ukazovat v overlayi.</b> Zásek trvá jeden snímek
    /// a přijde uprostřed pohybu — než se hráč podívá do menu, je dávno pryč a čísla už
    /// patří klidnému běhu. Do logu se naopak zapíše přesně ten snímek, který cukl.</para>
    ///
    /// <para>Vypisují se jen fáze nad desetinu milisekundy, aby byl řádek čitelný.</para>
    /// </remarks>
    private void ReportHitch(double frameMs)
    {
        if (frameMs < HitchBudgetMs || _hitchesLeft <= 0)
        {
            return;
        }

        _hitchesLeft--;

        var builder = new System.Text.StringBuilder();
        builder.Append(string.Create(CultureInfo.InvariantCulture, $"ZASEK {frameMs:F1} ms:"));

        for (int i = 0; i < _phases.PhaseCount; i++)
        {
            double ms = _phases.Last(i);

            if (ms >= 0.1)
            {
                builder.Append(string.Create(CultureInfo.InvariantCulture, $" {_phases.Name(i)}={ms:F1}"));
            }
        }

        // Úklid paměti. Zastaví všechna vlákna naráz, takže se v žádné fázi neprojeví —
        // a přesně tak vypadaly záseky z prvního běhu: velký čas snímku, prázdný rozpad.
        int gen0 = GC.CollectionCount(0) - _gcAtFrameStart[0];
        int gen1 = GC.CollectionCount(1) - _gcAtFrameStart[1];
        int gen2 = GC.CollectionCount(2) - _gcAtFrameStart[2];

        if (gen0 + gen1 + gen2 > 0)
        {
            builder.Append(string.Create(CultureInfo.InvariantCulture, $" UKLID gen0={gen0} gen1={gen1} gen2={gen2}"));
        }

        // Stav streamingu k tomu: zásek při rychlém letu bývá o tom, kolik se toho zrovna
        // nahrává, ne o kreslení.
        builder.Append(string.Create(
            CultureInfo.InvariantCulture,
            $" | chunku={LoadedChunks} LOD={_farTerrain?.TileCount ?? 0}"
            + $" fronta={_farTerrain?.PendingTiles ?? 0} odlozeno={_farTerrain?.RetiredTiles ?? 0}"));

        Log.Info(builder.ToString());

        if (_hitchesLeft == 0)
        {
            Log.Info("Dalsi zaseky se uz nevypisuji (strop 40).");
        }
    }

    /// <summary>Rozpad času hlavního vlákna po fázích. Čte to selftest i overlay.</summary>
    public string PhaseBreakdown()
    {
        var builder = new System.Text.StringBuilder();

        for (int i = 0; i < _phases.PhaseCount; i++)
        {
            FrameStats stats = _phases.Stats(i);
            builder.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"  {_phases.Name(i),-16}: p50 {stats.P50Ms,7:F3}  p99 {stats.P99Ms,8:F3}  max {stats.MaxMs,8:F2} ms"));
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>
    /// Zapne nebo vypne čekání na obnovu monitoru.
    /// </summary>
    /// <remarks>
    /// <para>Ve Vulkanu se to nastavovalo předěláním swapchainu, protože o zobrazování
    /// rozhoduje jeho režim. V OpenGL je to vlastnost kontextu a mění se za běhu jedním
    /// voláním — swapchain tu žádný není a předělávat cíle kvůli tomu je zbytečné.</para>
    ///
    /// <para><b>Bez tohohle přepínač v dev menu nedělal nic</b> a hra jela na tom, co si
    /// nastavila OpenTK při zakládání okna.</para>
    /// </remarks>
    /// <summary>
    /// Přepne čekání na monitor.
    /// </summary>
    /// <remarks>
    /// <b>Nejde přes GameWindow.VSync.</b> To sahá na grafický kontext, který okno bez API
    /// nemá — skončí to výjimkou „Cannot control vsync when running with ContextAPI.NoAPI".
    /// Ve Vulkanu o čekání rozhoduje režim zobrazování swapchainu, takže se swapchain
    /// postaví znovu s novým nastavením.
    /// </remarks>
    private void ApplyVsync() => _renderer?.HandleResize(ClientSize.X, ClientSize.Y, _vsync);

    protected override void OnResize(ResizeEventArgs e)
    {
        base.OnResize(e);

        // Viewport se ve Vulkanu nenastavuje tady — je dynamickým stavem a zapisuje se
        // do příkazového bufferu na začátku každého snímku. Tady se jen předělá swapchain.
        if (e.Width > 0 && e.Height > 0)
        {
            _renderer?.HandleResize(e.Width, e.Height, _vsync);
            _chunkRenderer?.HandleResize();
        }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);

        if (_pauseMenuOpen)
        {
            return;
        }

        if (_selftestFrames == 0 && _startupStage != StartupStage.Playing)
        {
            return;
        }

        if (HasActiveModScreen)
        {
            DispatchModUiInput(new Tesseris.ModApi.ModUiInput(
                Tesseris.ModApi.ModUiInputKind.Wheel,
                Tesseris.ModApi.ModInputPhase.Pressed,
                (int)MouseState.Position.X,
                (int)MouseState.Position.Y,
                WheelX: e.OffsetX,
                WheelY: e.OffsetY));
            return;
        }

        // S otevřeným inventářem roluje kolečko pravý sloupec, ne pás — pás je v tu chvíli
        // vidět celý a přepínat se dá klikem.
        if (_inventoryScreen?.Open == true)
        {
            if (_inventoryScreen.Creative && _inventoryScreen.Furnace is null
                && _inventoryScreen.Chest is null)
            {
                _inventoryScreen.CreativeScroll = Math.Clamp(
                    _inventoryScreen.CreativeScroll - (int)e.OffsetY,
                    0,
                    _inventoryScreen.CreativeMaxScroll(ClientSize.X, ClientSize.Y));
                return;
            }

            if (_recipes is not null && _inventoryScreen.BookOpen)
            {
                int rows = _inventoryScreen.BookMaxScroll(
                    _inventoryScreen.FilteredRecipeCount(_recipes), ClientSize.X, ClientSize.Y);

                _inventoryScreen.BookScroll =
                    Math.Clamp(_inventoryScreen.BookScroll - (int)e.OffsetY, 0, rows);
            }

            return;
        }

        _inventory?.Scroll(-(int)e.OffsetY);
    }

    /// <summary>Textová pole úvodního menu přijímají skutečné znaky, ne mapování kláves.</summary>
    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);

        if (_modCommandOpen)
        {
            string text = e.AsString;
            if (_ignoreNextCommandSlashText && text == "/")
            {
                _ignoreNextCommandSlashText = false;
                return;
            }

            _ignoreNextCommandSlashText = false;
            foreach (char character in text)
            {
                if (!char.IsControl(character) && _modCommandText.Length < 512)
                {
                    _modCommandText += character;
                }
            }
            return;
        }

        if (HasActiveModScreen)
        {
            DispatchModUiInput(new Tesseris.ModApi.ModUiInput(
                Tesseris.ModApi.ModUiInputKind.Text,
                Tesseris.ModApi.ModInputPhase.Pressed,
                (int)MouseState.Position.X,
                (int)MouseState.Position.Y,
                Text: e.AsString));
            return;
        }

        if (_inventoryScreen?.Open == true && _inventoryScreen.SearchFocused)
        {
            _inventoryScreen.AppendSearchText(e.AsString);
            return;
        }

        if (_selftestFrames == 0 && _startupStage == StartupStage.MainMenu
            && !_modManagerOpen && !_graphicsSettingsOpen)
        {
            _mainMenu?.AppendText(e.AsString);
        }
    }

    protected override void OnKeyDown(KeyboardKeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_inventoryScreen?.Open == true && _inventoryScreen.SearchFocused)
        {
            if (e.Key == Keys.Backspace) _inventoryScreen.BackspaceSearch();
            else if (e.Key == Keys.Delete) _inventoryScreen.ClearSearch();
            return;
        }
        if (HandleModCommandKey(e))
        {
            return;
        }
        DispatchModScreenKey(e, Tesseris.ModApi.ModInputPhase.Pressed);
    }

    protected override void OnKeyUp(KeyboardKeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (_modCommandOpen)
        {
            return;
        }
        DispatchModScreenKey(e, Tesseris.ModApi.ModInputPhase.Released);
    }

    private bool HandleModCommandKey(KeyboardKeyEventArgs e)
    {
        if (_startupStage != StartupStage.Playing || _mods is null)
        {
            return false;
        }

        if (!_modCommandOpen)
        {
            if (e.Key != Keys.Slash || HasActiveModScreen || _inventoryScreen?.Open == true || _menu.Visible)
            {
                return false;
            }

            _modCommandOpen = true;
            _modCommandText = "/";
            _ignoreNextCommandSlashText = true;
            _mining?.Stop();
            CursorState = CursorState.Normal;
            return true;
        }

        switch (e.Key)
        {
            case Keys.Enter:
            case Keys.KeyPadEnter:
                ExecuteModCommand();
                return true;
            case Keys.Backspace:
                if (_modCommandText.Length > 1)
                {
                    int[] elements = StringInfo.ParseCombiningCharacters(_modCommandText);
                    if (elements.Length > 1)
                    {
                        _modCommandText = _modCommandText[..elements[^1]];
                    }
                }
                return true;
            case Keys.Tab:
                IReadOnlyList<string> completions = _mods.Client.Commands.Complete(_modCommandText, 1);
                if (completions.Count == 1)
                {
                    _modCommandText = completions[0] + " ";
                }
                return true;
            default:
                return true;
        }
    }

    private void ExecuteModCommand()
    {
        if (_mods is null)
        {
            CloseModCommand();
            return;
        }

        if (TryExecuteMobCommand(_modCommandText)
            || TryExecuteStructureCommand(_modCommandText)
            || TryExecuteTimeCommand(_modCommandText)
            || TryExecuteItemsCommand(_modCommandText)
            || TryExecuteFactoryCommand(_modCommandText)
            || TryExecutePowerCommand(_modCommandText)
            || TryExecuteNightCommand(_modCommandText)
            || TryExecuteNightMaxCommand(_modCommandText))
        {
            while (_modCommandMessages.Count > 8) _modCommandMessages.RemoveAt(0);
            _modCommandMessageSeconds = 10f;
            CloseModCommand();
            return;
        }

        var output = new WindowModCommandOutput(_modCommandMessages);
        Tesseris.Game.Modding.Client.ModCommandExecutionResult result =
            _mods.Client.Commands.Execute(_modCommandText, output, playerId: 0, isRemote: false);
        if (result.Diagnostic is { Length: > 0 }
            && (result.Status is Tesseris.Game.Modding.Client.ModCommandStatus.NotHandled
                or Tesseris.Game.Modding.Client.ModCommandStatus.Disabled))
        {
            _modCommandMessages.Add(result.Diagnostic);
        }
        while (_modCommandMessages.Count > 8)
        {
            _modCommandMessages.RemoveAt(0);
        }
        _modCommandMessageSeconds = 8f;
        CloseModCommand();
    }

    /// <summary>
    /// <c>/mobs</c> — přehlídka všech druhů. Postaví každý typ z <see cref="MobDefinitions"/>
    /// do mřížky před hráče, aby šlo na jednom místě zkontrolovat modely, skiny i animace.
    /// </summary>
    /// <remarks>
    /// Vzniklo proto, že část druhů se ve světě prakticky nedá potkat: nepřátelé se rodí jen
    /// v noci, řada zvířat má úzký seznam biomů a barevné ovce jsou vzácné. Kdo chce vidět,
    /// jestli mají správnou texturu a chodí, jak mají, nemá je jak najít.
    /// </remarks>
    /// <summary>
    /// Odsune kameru za hráče, případně před něj, a nastaví, jestli se má kreslit postava.
    /// </summary>
    /// <remarks>
    /// Postup je stejný jako v Luanti (<c>references/luanti/src/client/camera.cpp:414-445</c>):
    /// kamera couvá po ose pohledu a jakmile by se dostala do pevného bloku, vrátí se kousek
    /// zpátky. Pohled zepředu se dělá otočením směru — kamera pak stojí před hráčem a dívá se
    /// na něj. Natočení kamery se přitom nemění, takže míření zůstává tam, kam ukazuje zaměřovač.
    /// </remarks>
    /// <summary>
    /// Ohlásí kolonii, že hráč změnil blok.
    /// </summary>
    /// <remarks>
    /// <b>Bez tohohle pro ně tvoje stavby neexistují.</b> Navigační mřížka se stavěla jednou
    /// na chunk a měnila se jen tehdy, když kopal kolonista — hráčovo stavění ani bourání se
    /// do ní nepromítlo. Kolonisté proto procházeli skrz radnici i skrz každou postavenou zeď
    /// a chodili po vzduchu tam, kde hráč vykopal díru.
    ///
    /// Kopnutí stojí naměřených 0,0028 ms proti 3,34 ms plné přestavby, takže se to vyplatí
    /// hlásit u každé změny.
    /// </remarks>
    private void NotifyColonyOfBlockChange(int x, int y, int z)
    {
        if (_world is not null)
        {
            _colony.OnBlockChanged(_world, _world.Registry, new Vector3i(x, y, z));
        }
    }

    private void ApplyCameraView()
    {
        if (_chunkRenderer is null || _player is null) return;

        // REŽIM VELITELE PŘEBÍJÍ VŠECHNO OSTATNÍ. Přechod se posouvá po snímcích, protože
        // je to obrazová věc — svázat ho s tikem by při zpomalené simulaci vypadalo jako zásek.
        _commander.Update((float)_frameTimer.DeltaSeconds);

        if (_commander.Commanding || _commander.InTransition)
        {
            _chunkRenderer.Player = _commander.Blend > 0.15f ? _chunkRenderer.Player : null;
            _camera.Position = _commander.CameraPosition(_player.EyePosition);
            _camera.ViewDirection = _commander.ViewDirection(_camera.Forward);
            return;
        }

        if (_cameraView == CameraView.First)
        {
            _chunkRenderer.Player = null;
            _camera.ViewDirection = null;
            return;
        }

        Vector3 eye = _camera.Position;
        Vector3 direction = _camera.Forward;

        // POHLED ZEPŘEDU OTÁČÍ I SMĚR KOUKÁNÍ. Luanti to dělá jedním `m_camera_direction *= -1`
        // (camera.cpp:418), čímž se kamera přesune před hráče a zároveň se otočí zpátky na něj.
        // U nás je směr pohledu oddělený od míření, aby hráč pořád kopal dopředu.
        _camera.ViewDirection = _cameraView == CameraView.ThirdFront ? -direction : null;
        if (_cameraView == CameraView.ThirdFront) direction = -direction;

        // Couvání po krocích čtvrt bloku. Poslední volný krok vyhrává; jakmile paprsek narazí
        // do pevného bloku, kamera se zastaví o kus dřív, aby nekoukala skrz zeď.
        const float MinimumDistance = 0.6f;
        const float MaximumDistance = 3.2f;
        float distance = MinimumDistance;
        for (float step = MinimumDistance; step <= MaximumDistance; step += 0.25f)
        {
            Vector3 probe = eye - (direction * step);
            if (_world is not null && IsSolidAt(probe)) break;
            distance = step;
        }

        _camera.Position = eye - (direction * distance);

        // KLIPY JAKO V LUANTI (player_api): stání 0–79, chůze 168–187, kopání 189–198
        // a chůze s kopáním 200–219. Snímek běží v sekundách krát fps klipu, stejně jako
        // to dělá `set_animation` — ne podle ušlé vzdálenosti.
        bool walking = _playerWalkPhase > 0f;
        bool mining = _swing > 0f;
        B3dAnimationClip clip = (walking, mining) switch
        {
            (true, true) => B3dAnimationProfile.Character.Run,
            (true, false) => B3dAnimationProfile.Character.Walk,
            (false, true) => B3dAnimationProfile.Character.Graze,
            _ => B3dAnimationProfile.Character.Idle,
        };
        float span = MathF.Max(1f, clip.End - clip.Start);
        float frame = clip.Start + ((_playerAnimationTime * clip.Speed) % span);

        // NATOČENÍ POSTAVY. Model míří osou +Z, a naše otáčení dává rotate(0,0,1) = (sin θ, cos θ),
        // takže θ = atan2(fx, fz). Vodorovný forward z úhlu kamery φ je (cos φ, sin φ), z čehož
        // vyjde θ = 90° − φ. Dřív tu bylo −φ − 90°, tedy přesně o 180° vedle — v pohledu zezadu
        // proto byla postava čelem ke kameře.
        _chunkRenderer.Player = (
            InterpolatedFoot(),
            MathHelper.DegreesToRadians(90f - _camera.YawDegrees),
            frame,

            // Hlava kopíruje sklon pohledu, stejně jako to dělá `player_api` v Luanti.
            // Omezená je proto, že model nemá krk na to, aby se otočil kolmo vzhůru.
            Math.Clamp(MathHelper.DegreesToRadians(_camera.PitchDegrees), -1.1f, 1.1f));
    }

    /// <summary>
    /// Poloha očí pro tenhle snímek: mezi posledními dvěma tiky.
    /// </summary>
    /// <remarks>
    /// <para><b>Skok se neinterpoluje, ten se přeskočí.</b> Teleport, respawn nebo načtení
    /// světa posunou hráče o víc, než kolik ujde za jeden tik i v letu se sprintem
    /// (48,4 m/s je 0,81 bloku za tik). Kdyby se přes takový skok lerpovalo, kamera by přes
    /// půl světa plynule přeletěla. Práh se proto testuje na vzdálenosti, ne na výčtu
    /// volajících — teleport umí vyvolat i mod, o kterém tohle okno neví.</para>
    /// </remarks>
    private Vector3 InterpolatedEye()
    {
        Vector3 current = _player.EyePosition;

        if (!_hasPreviousEye)
        {
            return current;
        }

        if ((current - _previousEyePosition).LengthSquared > MaxInterpolatedStep * MaxInterpolatedStep)
        {
            return current;
        }

        return Vector3.Lerp(_previousEyePosition, current, (float)_simClock.InterpolationAlpha);
    }

    /// <summary>
    /// Změří, jestli render opravdu interpoluje a jestli ruka sedí na kameře.
    /// </summary>
    /// <remarks>
    /// <b>Rozhodovací test s binárním výsledkem.</b> Snímek, ve kterém neproběhl žádný tik,
    /// se před opravou nemohl nikam pohnout — poloha kamery byla přesná kopie tik-kvantované
    /// polohy hráče. Když se takový snímek přesto hne, interpolace funguje. A ruka musí mít
    /// oko přesně tam, kde je kamera; každý rozdíl je ten posun o tik, kvůli kterému mizela.
    /// </remarks>
    private void MeasureInterpolation()
    {
        if (!_interpProbe)
        {
            return;
        }

        _interpFrames++;

        // KRITÉRIUM: liší se kamera od tik-kvantované polohy hráče?
        //
        // Dva slepé konce, na které jsem cestou naletěl:
        //  1) "hnula se kamera mezi snímky" — projde i s rozbitou interpolací, protože svislá
        //     složka se každý snímek vyhlazuje o výstup na schod;
        //  2) totéž jen vodorovně — taky projde, protože selftest si hráče posouvá PŘÍMO
        //     každý snímek (viz let selftestu), takže tick úplně obchází.
        //
        // Tohle kritérium obojímu odolá: bez interpolace se do kamery zapisuje EyePosition
        // doslova, takže rozdíl je vždycky nula. Jakmile je nenulový, interpoluje se.
        Vector3 delta = _camera.Position - _player.EyePosition;
        delta.Y = 0f;
        bool moved = delta.LengthSquared > 1e-10f;
        _interpLastCamera = _camera.Position;

        if (moved)
        {
            _interpTicklessMoved++;
        }

        if (_ticksLastFrame == 0)
        {
            _interpTicklessFrames++;
        }

        // BLIKÁNÍ ANIMACE. Fáze chůze se nulovala v každém snímku bez tiku, takže postava
        // asi v polovině snímků spadla do klidu. Počítá se, kolikrát se stav chůze změnil —
        // při plynulé chůzi to má být pár přechodů, ne stovky.
        bool walkingNow = _playerWalkPhase > 0f;
        if (walkingNow != _interpWasWalking)
        {
            _interpWalkFlips++;
            _interpWasWalking = walkingNow;
        }

        if (_chunkRenderer?.Held is { } held
            && (held.Eye - _camera.Position).LengthSquared > 1e-8f)
        {
            _interpHandMismatch++;
        }

        if (_interpFrames % 400 == 0)
        {
            Log.Info($"SONDA INTERPOLACE: snimku {_interpFrames}, bez tiku {_interpTicklessFrames}; "
                + $"kamera se lisi od tik-polohy hrace {_interpTicklessMoved}x; "
                + $"ruka mimo kameru {_interpHandMismatch}x; "
                + $"animace prepnula chuze/klid {_interpWalkFlips}x");
        }
    }

    /// <summary>Měřit interpolaci? Jen pro ověření, že render opravdu interpoluje.</summary>
    private readonly bool _interpProbe =
        Environment.GetEnvironmentVariable("VOXELITY_INTERP_TEST") == "1";

    /// <summary>Měřit fyzické tělo kolonisty? Bez toho se na to selftest nedostane.</summary>
    private readonly bool _bodyProbe =
        Environment.GetEnvironmentVariable("VOXELITY_BODY_TEST") == "1";

    private int _bodyProbeTicks;
    private int _bodyProbeSubCellMoves;
    private int _bodyProbeWholeCellJumps;
    private int _bodyProbeDiagonal;
    private int _bodyProbeOverlaps;
    private int _bodyProbeAirborne;
    private float _bodyProbeLongestMove;
    private float _bodyProbeClosestPair = float.MaxValue;
    private Vector3[] _bodyProbePrevious = [];

    /// <summary>Kolik kolonistů už sonda zná. Nováčka nesmí započítat jako skok.</summary>
    private int _bodyProbeKnown;

    /// <summary>V jakém stavu se udělal nejdelší posun. Bez toho se příčina jen hádá.</summary>
    private ColonistState _bodyProbeLongestState;

    /// <inheritdoc cref="_bodyProbeLongestState"/>
    private bool _bodyProbeLongestAirborne;

    /// <summary>
    /// Změří v běžící hře, jestli má kolonista opravdu fyzické tělo.
    /// </summary>
    /// <remarks>
    /// <para><b>Kritéria jsou taková, aby neprošla u rozbité věci</b>.
    /// „Kolonisté se hýbou" projde vždycky — hýbali se i po mřížce. Rozhoduje poměr posunů
    /// MENŠÍCH než buňka k posunům o CELOU buňku: po mřížce byl každý posun přesně 1,0
    /// a dílčích byla nula. Stejně tak u kolize: „nejsou v jedné buňce" je slabé, protože
    /// dva lidé můžou stát ve dvou sousedních buňkách a přesto v sobě; měří se proto
    /// skutečná vzdálenost těl.</para>
    ///
    /// <para><b>Prochází se každý s každým</b>, což je kvadratické — a je to schválně:
    /// sonda si nemá půjčovat tentýž prostorový index, který má ověřit. Sonda se zapíná
    /// proměnnou prostředí a v normální hře neběží.</para>
    /// </remarks>
    private void MeasureColonistBodies()
    {
        if (!_bodyProbe)
        {
            return;
        }

        ColonySimulation people = _colony.Colonists;
        int count = people.Count;

        if (count == 0)
        {
            return;
        }

        // PŘÍCHOD NOVÉHO ČLOVĚKA NENÍ POHYB. Nově příchozí kolonista nemá předchozí polohu,
        // takže by se jeho první tik započítal jako skok z počátku souřadnic — naměřeno
        // „nejdelší za tik 10,512 bloku" proti 0,077, kolik dovolí chůze.
        //
        // MĚŘÍ SE ALE I TIK, VE KTERÉM NĚKDO PŘIŠEL — jen se v něm přeskočí ten jeden nováček,
        // ne celý tik. Původní verze se z takového tiku rovnou vrátila, což se se třemi lidmi
        // nikdy neprojevilo (přicházeli jednou za 300 tiků), ale se zapnutou sondou
        // VOXELITY_COLONISTS chodí jeden KAŽDÝ tik: prvních dvě stě tiků se proto neměřilo
        // vůbec, polohy v poli zastaraly a sonda pak ohlásila posun 8,777 bloku za tik
        // a 6 549 překryvů, které ve skutečnosti nebyly. Simulace byla v pořádku, měřidlo ne.
        if (_bodyProbePrevious.Length < count)
        {
            Array.Resize(ref _bodyProbePrevious, Math.Max(count, 64));
        }

        int prisliTentoTik = _bodyProbeKnown;
        for (int id = _bodyProbeKnown; id < count; id++)
        {
            _bodyProbePrevious[id] = people.PositionOf(id);
        }

        _bodyProbeKnown = count;
        _bodyProbeTicks++;

        for (int id = 0; id < prisliTentoTik; id++)
        {
            Vector3 now = people.PositionOf(id);
            Vector3 delta = now - _bodyProbePrevious[id];
            _bodyProbePrevious[id] = now;

            if (!people.IsOnGround(id))
            {
                _bodyProbeAirborne++;
            }

            float travel = MathF.Sqrt((delta.X * delta.X) + (delta.Z * delta.Z));
            if (travel <= 1e-4f)
            {
                continue;
            }

            if (travel > _bodyProbeLongestMove)
            {
                _bodyProbeLongestMove = travel;

                // KDO A V JAKÉM STAVU. Bez tohohle se dá jen hádat, kde se rychlost sečetla
                // dvakrát — a hádání poslepu je přesně to, co v tomhle repu vyrábí chyby.
                _bodyProbeLongestState = people.StateOf(id);
                _bodyProbeLongestAirborne = !people.IsOnGround(id);
            }

            // TOHLE JE TO KRITÉRIUM. Po mřížce byl každý posun přesně o celou buňku.
            if (travel < 0.9f)
            {
                _bodyProbeSubCellMoves++;
            }
            else
            {
                _bodyProbeWholeCellJumps++;
            }

            if (MathF.Abs(delta.X) > 1e-4f && MathF.Abs(delta.Z) > 1e-4f)
            {
                _bodyProbeDiagonal++;
            }
        }

        // KOLIZE: skutečná vzdálenost těl, ne shoda buněk.
        for (int a = 0; a < count; a++)
        {
            for (int b = a + 1; b < count; b++)
            {
                Vector3 first = people.PositionOf(a);
                Vector3 second = people.PositionOf(b);

                if (MathF.Abs(first.Y - second.Y) >= 1f)
                {
                    continue;
                }

                float dx = first.X - second.X;
                float dz = first.Z - second.Z;
                float gap = MathF.Sqrt((dx * dx) + (dz * dz));

                _bodyProbeClosestPair = MathF.Min(_bodyProbeClosestPair, gap);

                if (gap < ColonistBody.HalfWidth)
                {
                    _bodyProbeOverlaps++;
                }
            }
        }

        if (_bodyProbeTicks % 300 == 0)
        {
            int total = _bodyProbeSubCellMoves + _bodyProbeWholeCellJumps;
            float subCellShare = total > 0 ? _bodyProbeSubCellMoves * 100f / total : 0f;

            // KDE dav stojí, ne jen kolik jich je v sobě. Bez tohohle se dá jen hádat, jestli
            // se nemají kam rozejít, nebo jestli je rozestupování rozbité — a hádání poslepu
            // je v tomhle repu nejspolehlivější způsob, jak vyrobit další chybu.
            float minY = float.MaxValue;
            float maxY = float.MinValue;
            float minX = float.MaxValue;
            float maxX = float.MinValue;
            int naZemi = 0;
            int hlubokoPodRadnici = 0;
            float radniceY = _colony.TownHall.IsFounded ? _colony.TownHall.Cell.Y : float.NaN;

            for (int id = 0; id < count; id++)
            {
                Vector3 at = people.PositionOf(id);
                minY = MathF.Min(minY, at.Y);
                maxY = MathF.Max(maxY, at.Y);
                minX = MathF.Min(minX, at.X);
                maxX = MathF.Max(maxX, at.X);

                if (people.IsOnGround(id))
                {
                    naZemi++;
                }

                // KDO SPADL Z KOLONIE. Deset pater je víc, než jaký může být svah kolem
                // radnice; hlubší rozdíl znamená, že se člověk propadl, ne že sešel dolů.
                if (!float.IsNaN(radniceY) && at.Y < radniceY - 10f)
                {
                    hlubokoPodRadnici++;
                }
            }

            Log.Info($"SONDA TELA: {count} lidi, {_bodyProbeTicks} tiku. "
                + $"POSUNY dilcich {_bodyProbeSubCellMoves} ({subCellShare:F1} %), "
                + $"o celou bunku {_bodyProbeWholeCellJumps}, sikmych {_bodyProbeDiagonal}, "
                + $"nejdelsi za tik {_bodyProbeLongestMove:F3} bloku "
                + $"(chuze dovoli {ColonistBody.WalkSpeed * ColonistBody.StepSeconds:F3}, "
                + $"stav {_bodyProbeLongestState}, ve vzduchu {_bodyProbeLongestAirborne}). "
                + $"KOLIZE nejblizsi dvojice {_bodyProbeClosestPair:F3} bloku "
                + $"(sirka tela {ColonistBody.Width:F2}), prekryvu {_bodyProbeOverlaps}. "
                + $"VE VZDUCHU {_bodyProbeAirborne}x (skok nebo pad). "
                + $"ROZPTYL x {minX:F1}..{maxX:F1} ({maxX - minX:F1} bloku), "
                + $"y {minY:F1}..{maxY:F1}, na zemi {naZemi} z {count}, "
                + $"hluboko pod radnici (y {radniceY:F0}) {hlubokoPodRadnici}. "
                + $"CAS nejhorsi navigace {_worstNavigationMs:F2} ms, "
                + $"nejhorsi kolonie {_worstColonyMs:F2} ms (rozpocet tiku 8 ms); "
                + $"z navigace mrizka {_worstGridMs:F2}, portaly {_worstPortalsMs:F2}, "
                + $"hledani chunku {_worstCollectMs:F2} ms.");
        }
    }

    private Vector3 _interpLastCamera;
    private int _interpFrames;
    private int _interpTicklessFrames;
    private int _interpTicklessMoved;
    private int _interpHandMismatch;
    private bool _interpWasWalking;
    private int _interpWalkFlips;

    /// <summary>
    /// Poloha nohou pro tenhle snímek. Stejné pravidlo jako u očí, viz <see cref="InterpolatedEye"/>.
    /// </summary>
    /// <remarks>
    /// <b>Model postavy se musí hýbat stejně plynule jako kamera.</b> Kreslil se z tik-kvantované
    /// polohy, zatímco kamera už interpolovala — ve třetí osobě proto postava poskakovala
    /// PROTI plynulému obrazu. Interpolace kamery to nezpůsobila, ale zvýraznila: dokud
    /// poskakovalo obojí stejně, nebylo to na postavě vidět.
    /// </remarks>
    private Vector3 InterpolatedFoot()
    {
        Vector3 current = _player.Position;

        if (!_hasPreviousEye
            || (current - _previousTickFoot).LengthSquared > MaxInterpolatedStep * MaxInterpolatedStep)
        {
            return current;
        }

        return Vector3.Lerp(_previousTickFoot, current, (float)_simClock.InterpolationAlpha);
    }

    /// <summary>Nejdelší posun za tik, který se ještě smí interpolovat. Nad ním jde o skok.</summary>
    /// <remarks>Let se sprintem dělá 0,81 bloku za tik, takže dvojka je s rezervou nad tím.</remarks>
    private const float MaxInterpolatedStep = 2f;

    private bool IsSolidAt(Vector3 point)
    {
        if (_world is null) return false;
        ushort block = _world.GetBlock(
            (int)MathF.Floor(point.X), (int)MathF.Floor(point.Y), (int)MathF.Floor(point.Z));
        return block != BlockRegistry.Air && _world.Registry.Definition(block).Solid;
    }

    private bool TryExecuteMobCommand(string commandLine)
    {
        string[] arguments = commandLine.Trim().Split(
            ' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (arguments.Length == 0
            || !string.Equals(arguments[0], "/mobs", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            string action = arguments.Length > 1 ? arguments[1].ToLowerInvariant() : "show";
            switch (action)
            {
                case "show":
                    SpawnMobShowcase(arguments.Length > 2 ? float.Parse(arguments[2]) : 3.2f);
                    break;
                case "clear":
                    StructureMessage(_animals is null
                        ? "Svět ještě neběží."
                        : $"Odstraněno {_animals.RemoveAll()} zvířat.");
                    break;
                case "list":
                    StructureMessage($"Druhů celkem: {MobDefinitions.All.Count}");
                    foreach (string row in MobRows(8))
                        StructureMessage(row);
                    break;
                default:
                    StructureMessage("/mobs [show [ROZESTUP]] | clear | list");
                    break;
            }
        }
        catch (Exception error) when (error is InvalidOperationException or InvalidDataException
            or FormatException or ArgumentException)
        {
            StructureMessage(error.Message);
        }

        return true;
    }

    private void SpawnMobShowcase(float spacing)
    {
        if (_animals is null || _generator is null || _player is null)
            throw new InvalidOperationException("Svět ještě neběží.");
        if (!float.IsFinite(spacing) || spacing < 1f || spacing > 16f)
            throw new InvalidDataException("Rozestup musí být mezi 1 a 16.");

        // Přehlídka se staví PŘED hráče podle směru pohledu, ne na pevné souřadnice —
        // jinak by skončila za dohledem, případně v zemi na jiném terénu.
        Vector3 forward = _camera.Forward;
        forward.Y = 0f;
        forward = forward.LengthSquared < 1e-6f ? -Vector3.UnitZ : Vector3.Normalize(forward);
        Vector3 right = new(-forward.Z, 0f, forward.X);
        Vector3 origin = _player.Position + (forward * (spacing * 2f));

        const int columns = 8;
        _animals.RemoveAll();
        int placed = 0;
        for (int index = 0; index < MobDefinitions.All.Count; index++)
        {
            MobDefinition definition = MobDefinitions.All[index];
            Vector3 offset = (right * ((index % columns) - ((columns - 1) * 0.5f)) * spacing)
                + (forward * (index / columns) * (spacing + 1f));
            Vector3 spot = origin + offset;
            spot.Y = _generator.SurfaceHeight((int)MathF.Floor(spot.X), (int)MathF.Floor(spot.Z)) + 1.001f;
            _animals.AddForTest(definition.Id, spot, (uint)(7 + (index * 2)), frozen: true);
            placed++;
        }

        // Kolik jich mřížku doopravdy přežilo — kdyby se některý druh nezaložil, ať je to vidět
        // hned v příkazu a ne až tím, že ho hráč ve světě nenajde.
        StructureMessage(placed == _animals.Count
            ? $"Postaveno {placed} druhů v mřížce po {columns}, rozestup {spacing:0.#}. Stojí na místě."
            : $"POZOR: založeno {placed}, ale ve světě je {_animals.Count} zvířat.");
        foreach (string row in MobRows(columns))
            StructureMessage(row);
        if (!_creative)
            StructureMessage("Nepřátelé útočí — v kreativu (G) si tě nevšímají.");
    }

    private static IEnumerable<string> MobRows(int columns)
    {
        for (int row = 0; row * columns < MobDefinitions.All.Count; row++)
        {
            IEnumerable<string> names = MobDefinitions.All
                .Skip(row * columns)
                .Take(columns)
                .Select(definition => definition.Id.Split(':')[^1]);
            yield return $"řada {row + 1}: {string.Join(", ", names)}";
        }
    }

    /// <summary>
    /// Nastaví denní dobu: <c>/time night</c>, <c>day</c>, <c>noon</c>, <c>midnight</c>,
    /// <c>dawn</c>, <c>dusk</c>, nebo číslo 0 až 1.
    /// </summary>
    /// <remarks>
    /// Bez tohohle se noční osvětlení testovalo čekáním, až se slunce samo posune —
    /// dvacet minut na jeden pokus. Přepínač v dev menu (F4) sice čas nastavit umí,
    /// ale posuvníkem se konkrétní hodnota netrefí.
    /// </remarks>
    /// <summary>
    /// Nastaví, kolik je v noci vidět: <c>/night 0.35</c>. Bez argumentu vypíše hodnotu.
    /// </summary>
    /// <summary>
    /// Nastaví, co znamená plný noční jas: <c>/nightmax 0.4</c>.
    /// </summary>
    /// <remarks>
    /// Je to strop, ke kterému se vztahuje <c>/night</c>. Když je 0,4, pak <c>/night 1</c>
    /// dá 0,4 měsíčního světla — celý posuvník se tím rozprostře do rozsahu, který dává
    /// v dané scéně smysl, místo aby horní půlka byla nepoužitelně přesvětlená.
    /// </remarks>
    private bool TryExecuteNightMaxCommand(string commandLine)
    {
        string[] arguments = commandLine.Trim().Split(
            ' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (arguments.Length == 0
            || !string.Equals(arguments[0], "/nightmax", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (_chunkRenderer is null)
        {
            return false;
        }

        DayCycle day = _chunkRenderer.Day;

        if (arguments.Length < 2)
        {
            StructureMessage(
                $"Plny nocni jas (/night 1) = {day.NightBrightnessCeiling:F2}. "
                + $"Ted /night {day.NightBrightness:F2}, mesicniho svetla {day.Moonlight:F3}. "
                + "Pouziti: /nightmax <0.05-1>");
            return true;
        }

        if (!float.TryParse(arguments[1], System.Globalization.NumberStyles.Float,
                CultureInfo.InvariantCulture, out float hodnota))
        {
            StructureMessage($"Neni to cislo: {arguments[1]}");
            return true;
        }

        hodnota = Math.Clamp(hodnota, 0.05f, 1f);
        day.NightBrightnessCeiling = hodnota;
        _playerOptions.NightBrightnessCeiling = hodnota;

        StructureMessage(
            $"Plny nocni jas = {hodnota:F2}. Pri /night {day.NightBrightness:F2} "
            + $"vychazi mesicniho svetla {day.Moonlight:F3}.");

        return true;
    }

    /// <summary>
    /// <c>/items N</c> — naseje kolem hráče N instancovaných itemů.
    /// </summary>
    /// <remarks>
    /// Měřicí nástroj k povinnému benchmarku T4. Bez argumentu vypíše, kolik jich je
    /// a co to stojí.
    /// </remarks>
    private bool TryExecuteItemsCommand(string commandLine)
    {
        string[] parts = commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || !parts[0].Equals("/items", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (parts.Length < 2)
        {
            StructureMessage(string.Create(
                CultureInfo.InvariantCulture,
                $"Instancovanych itemu: {_instancedItemProbe}, kresli se {_chunkRenderer?.InstancedItemCount ?? 0}, "
                + $"mimo zaber {_chunkRenderer?.InstancedItemsCulled ?? 0}, "
                + $"nahrani {_chunkRenderer?.InstancedItemsUploadMs ?? 0.0:F3} ms. Pouziti: /items 20000"));
            return true;
        }

        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int count) || count < 0)
        {
            StructureMessage("Pouziti: /items 20000 (0 vypne)");
            return true;
        }

        _instancedItemProbe = Math.Min(count, 200_000);
        StructureMessage(string.Create(
            CultureInfo.InvariantCulture,
            $"Instancovanych itemu: {_instancedItemProbe}."));
        return true;
    }

    /// <summary>
    /// <c>/factory</c> — postaví u hráče drtič, dva pásy a vkládač.
    /// </summary>
    /// <remarks>
    /// <para>Ruční stavění myší je práce na T8; tohle je nejmenší způsob, jak projet scénář
    /// přejímky M0 — postavit linku, nasypat na ni rudu a vidět, že se z ní stane prach
    /// a jeden člověk se uvolní.</para>
    ///
    /// <para>Drtič se staví <b>nejdřív ručně</b>, tedy s člověkem u něj. Napojení pásů
    /// a proudu je krok, který ho zautomatizuje a toho člověka pustí — a to je ten okamžik,
    /// o kterém je celá hra.</para>
    /// </remarks>
    private bool TryExecuteFactoryCommand(string commandLine)
    {
        string[] parts = commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || !parts[0].Equals("/factory", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (_world is null)
        {
            return false;
        }

        ushort ore = _world.Registry.IndexOf("tesseris:iron_ore");
        ushort dust = _world.Registry.IndexOf("tesseris:cobblestone");

        if (ore == BlockRegistry.Air || dust == BlockRegistry.Air)
        {
            // Náhrada, když se ta konkrétní jména v obsahu nejmenují takhle: aspoň něco
            // rozeznatelného, ať linka jde vyzkoušet.
            ore = 1;
            dust = 2;
        }

        var at = new Vector3i(
            (int)MathF.Floor(_player.Position.X) + 3,
            (int)MathF.Floor(_player.Position.Y),
            (int)MathF.Floor(_player.Position.Z));

        int crusher = _colony.PlaceCrusher(at, ore, dust);

        // Nejdřív ručně: postaví se k němu člověk.
        bool manned = _colony.TryAssignOperator(crusher, colonist: 0);

        BeltSegment input = _colony.PlaceBelt(at + new Vector3i(-1, 0, 0), cells: 6);
        BeltSegment output = _colony.PlaceBelt(at + new Vector3i(1, 0, 0), cells: 6);

        _colony.Inserters.AddBeltToMachine(at + new Vector3i(-1, 0, 0), input, crusher);
        _colony.CountInserter();
        _colony.Inserters.AddMachineToBelt(
            _colony.Machines, at + new Vector3i(1, 0, 0), crusher, output);
        _colony.CountInserter();

        // Kam sonda sáhne, až bude zkoušet bourání.
        _factoryProbeCell = at + new Vector3i(-1, 0, 0);
        _factoryProbeOutputCell = at + new Vector3i(1, 0, 0);

        // RUDA SE SYPE POSTUPNĚ, ne naráz. Pás vyžaduje mezi itemy rozestup, takže šest
        // pokusů v jednom tiku uspěje jenom první — na to jsem už jednou narazil
        // v benchmarku pásů.
        _factoryFeedBelt = input;
        _factoryFeedItem = ore;
        _factoryFeedRemaining = 6;
        int loaded = _colony.TryPushOntoBelt(input, ore) ? 1 : 0;
        _factoryFeedRemaining -= loaded;

        StructureMessage(string.Create(
            CultureInfo.InvariantCulture,
            $"Drtic na {at}, dva pasy, vkladace. Rudy na pasu {loaded}, "
            + $"u stroje stoji clovek: {manned}. Napoj proud prikazem /power."));

        return true;
    }

    /// <summary>
    /// <c>/power</c> — zapne proud všem strojům.
    /// </summary>
    /// <remarks>
    /// Tohle je ten okamžik z kritéria T5: stroj má pásy i proud, takže se zautomatizuje
    /// a člověk u něj přestane být potřeba. Počítadlo volných lidí skočí nahoru.
    /// </remarks>
    private bool TryExecutePowerCommand(string commandLine)
    {
        string[] parts = commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || !parts[0].Equals("/power", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        int before = _colony.FreeColonists;

        for (int machine = 0; machine < _colony.Machines.Count; machine++)
        {
            if (_colony.Machines.IsRemoved(machine))
            {
                continue;
            }

            BeltSegment? input = null;
            BeltSegment? output = null;

            // Pásy vedle stroje. Stavěly se na sousední buňky, takže se tam i hledají.
            Vector3i cell = _colony.Machines.CellOf(machine);
            input = _colony.BeltAt(cell + new Vector3i(-1, 0, 0));
            output = _colony.BeltAt(cell + new Vector3i(1, 0, 0));

            if (input is not null)
            {
                _colony.Machines.SetInputBelt(machine, input);
            }

            if (output is not null)
            {
                _colony.Machines.SetOutputBelt(machine, output);
            }

            _colony.Machines.SetPowered(machine, true);
        }

        StructureMessage(string.Create(
            CultureInfo.InvariantCulture,
            $"Proud zapnut. Volnych lidi pred: {before}, uvolnila automatizace celkem: "
            + $"{_colony.FreedByAutomation}. Sleduj panel v rezimu velitele (Tab)."));

        return true;
    }

    private bool TryExecuteNightCommand(string commandLine)
    {
        string[] arguments = commandLine.Trim().Split(
            ' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (arguments.Length == 0
            || !string.Equals(arguments[0], "/night", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (_chunkRenderer is null)
        {
            return false;
        }

        if (arguments.Length < 2)
        {
            StructureMessage(
                $"Videt v noci: {_chunkRenderer.Day.NightBrightness:F2} "
                + $"(0 = uplna tma, 1 = denni jas). Pouziti: /night <0-1>");
            return true;
        }

        if (!float.TryParse(arguments[1], System.Globalization.NumberStyles.Float,
                CultureInfo.InvariantCulture, out float hodnota))
        {
            StructureMessage($"Neni to cislo: {arguments[1]}");
            return true;
        }

        hodnota = Math.Clamp(hodnota, 0f, 1f);
        _chunkRenderer.Day.NightBrightness = hodnota;
        _playerOptions.NightBrightness = hodnota;

        StructureMessage(
            $"Videt v noci: {hodnota:F2}, mesicniho svetla ted {_chunkRenderer.Day.Moonlight:F3}.");

        return true;
    }

    private bool TryExecuteTimeCommand(string commandLine)
    {
        string[] arguments = commandLine.Trim().Split(
            ' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (arguments.Length == 0
            || !string.Equals(arguments[0], "/time", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (_chunkRenderer is null)
        {
            return false;
        }

        DayCycle day = _chunkRenderer.Day;

        if (arguments.Length < 2)
        {
            StructureMessage(
                $"Denni doba {day.TimeOfDay:F3} ({(day.Running ? "bezi" : "stoji")}). "
                + "Pouziti: /time night|midnight|dawn|day|noon|dusk|stop|run|<0-1>");
            return true;
        }

        string value = arguments[1].ToLowerInvariant();

        switch (value)
        {
            case "stop":
                day.Running = false;
                StructureMessage("Cas zastaven.");
                return true;

            case "run":
                day.Running = true;
                StructureMessage("Cas bezi.");
                return true;
        }

        // Nula je pulnoc, 0,25 vychod, 0,5 poledne, 0,75 zapad — viz DayCycle.TimeOfDay.
        float? cil = value switch
        {
            "midnight" or "pulnoc" => 0.00f,
            "night" or "noc" => 0.92f,
            "dawn" or "svitani" => 0.25f,
            "day" or "den" or "morning" or "rano" => 0.35f,
            "noon" or "poledne" => 0.50f,
            "dusk" or "soumrak" or "evening" or "vecer" => 0.75f,
            _ => float.TryParse(value, System.Globalization.NumberStyles.Float,
                    CultureInfo.InvariantCulture, out float parsed)
                ? parsed
                : null,
        };

        if (cil is not { } cas)
        {
            StructureMessage($"Neznama denni doba: {value}");
            return true;
        }

        cas -= MathF.Floor(cas);
        day.TimeOfDay = cas;

        StructureMessage(
            $"Denni doba {cas:F3}, denniho svetla {day.Daylight:F2}, mesicniho {day.Moonlight:F2}.");

        return true;
    }

    private bool TryExecuteStructureCommand(string commandLine)
    {
        string[] arguments = commandLine.Trim().Split(
            ' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (arguments.Length == 0
            || !string.Equals(arguments[0], "/structure", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            string action = arguments.Length > 1 ? arguments[1].ToLowerInvariant() : "help";
            switch (action)
            {
                case "pos1":
                    _structureCornerOne = StructureTarget();
                    StructureMessage($"Bod 1: {_structureCornerOne.Value.X}, {_structureCornerOne.Value.Y}, {_structureCornerOne.Value.Z}");
                    break;
                case "pos2":
                    _structureCornerTwo = StructureTarget();
                    StructureMessage($"Bod 2: {_structureCornerTwo.Value.X}, {_structureCornerTwo.Value.Y}, {_structureCornerTwo.Value.Z}");
                    break;
                case "save":
                {
                    if (arguments.Length < 3) throw new InvalidDataException("Použití: /structure save NAZEV");
                    if (_world is null || _structureCornerOne is null || _structureCornerTwo is null)
                    {
                        throw new InvalidOperationException("Nejdřív označ /structure pos1 a /structure pos2.");
                    }
                    StructureTemplate template = StructureTemplate.Capture(
                        _world, _structureCornerOne.Value, _structureCornerTwo.Value, arguments[2]);
                    string path = StructureTemplateStore.Save(template);
                    StructureMessage($"Uloženo {template.Blocks.Count} bloků: {path}");
                    break;
                }
                case "place":
                {
                    if (arguments.Length < 3 || _world is null || _streamer is null)
                        throw new InvalidDataException("Použití: /structure place NAZEV [OTOCENI 0-3]");
                    int rotation = arguments.Length > 3 ? int.Parse(arguments[3]) & 3 : 0;
                    Vector3i origin = StructureTarget() + Vector3i.UnitY;
                    StructureTemplate template = StructureTemplateStore.Load(arguments[2]);
                    int placed = template.Place(_world, origin, rotation);
                    foreach (StructureCell cell in template.Blocks)
                    {
                        _streamer.InvalidateInteractiveBlock(
                            origin.X + cell.X - template.AnchorX,
                            origin.Y + cell.Y - template.AnchorY,
                            origin.Z + cell.Z - template.AnchorZ);
                    }
                    StructureMessage($"Postaveno '{template.Name}': {placed} bloků.");
                    break;
                }
                case "spawn":
                {
                    if (arguments.Length < 4)
                        throw new InvalidDataException("Použití: /structure spawn NAZEV on|off [SANCE%] [ROZESTUP] [MINY] [MAXY] [BIOMY]");
                    StructureTemplate template = StructureTemplateStore.Load(arguments[2]);
                    template.Spawn.Enabled = arguments[3].Equals("on", StringComparison.OrdinalIgnoreCase);
                    if (arguments.Length > 4) template.Spawn.ChancePercent = Math.Clamp(int.Parse(arguments[4]), 0, 100);
                    if (arguments.Length > 5) template.Spawn.SpacingChunks = Math.Clamp(int.Parse(arguments[5]), 4, 64);
                    if (arguments.Length > 6) template.Spawn.MinY = int.Parse(arguments[6]);
                    if (arguments.Length > 7) template.Spawn.MaxY = int.Parse(arguments[7]);
                    if (arguments.Length > 8)
                        template.Spawn.Biomes = arguments[8].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    StructureTemplateStore.Save(template);
                    StructureMessage($"Spawn '{template.Name}': {(template.Spawn.Enabled ? "zapnut" : "vypnut")}, {template.Spawn.ChancePercent}% / {template.Spawn.SpacingChunks} chunků.");
                    StructureMessage("Změna se projeví v novém světě nebo nových neprozkoumaných oblastech.");
                    break;
                }
                case "loot":
                {
                    if (arguments.Length < 4)
                        throw new InvalidDataException("Použití: /structure loot NAZEV TABULKA [random|fixed]");
                    StructureTemplate template = StructureTemplateStore.Load(arguments[2]);
                    bool random = arguments.Length < 5 || !arguments[4].Equals("fixed", StringComparison.OrdinalIgnoreCase);
                    template.Loot = template.Loot.Select(marker => marker with
                    {
                        Table = arguments[3],
                        Random = random,
                    }).ToList();
                    StructureTemplateStore.Save(template);
                    StructureMessage($"Loot '{arguments[3]}' nastaven pro {template.Loot.Count} truhel.");
                    break;
                }
                case "list":
                {
                    IReadOnlyList<StructureTemplate> templates = StructureTemplateStore.LoadAll();
                    StructureMessage(templates.Count == 0
                        ? "Zatím není uložená žádná struktura."
                        : "Struktury: " + string.Join(", ", templates.Select(value => value.Name)));
                    break;
                }
                default:
                    StructureMessage("STRUCTURE CREATOR: postav návrh a zamiř na dva protilehlé rohy.");
                    StructureMessage("/structure pos1 | pos2 | save NAZEV | place NAZEV [0-3]");
                    StructureMessage("/structure spawn NAZEV on|off [SANCE] [ROZESTUP] [MINY] [MAXY] [BIOMY]");
                    StructureMessage("/structure loot NAZEV TABULKA [random|fixed] | list");
                    break;
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException
            or InvalidOperationException or FormatException)
        {
            StructureMessage("Chyba: " + exception.Message);
        }
        return true;
    }

    private Vector3i StructureTarget()
    {
        if (_world is null || _player is null
            || !MicroRaycast.Cast(_world, _player.EyePosition, _camera.Forward,
                ReachDistance, out MicroHit hit, hitLiquid: false))
        {
            throw new InvalidOperationException("Zamiř na blok v dosahu.");
        }
        return hit.Block;
    }

    private void StructureMessage(string message) => _modCommandMessages.Add(message);

    private void CloseModCommand()
    {
        _modCommandOpen = false;
        _modCommandText = string.Empty;
        _ignoreNextCommandSlashText = false;
        if (_startupStage == StartupStage.Playing)
        {
            CursorState = CursorState.Grabbed;
            _ignoreNextMouseDelta = true;
        }
    }

    private void DispatchModScreenKey(
        KeyboardKeyEventArgs e,
        Tesseris.ModApi.ModInputPhase phase)
    {
        if (_mods is null)
        {
            return;
        }

        Tesseris.ModApi.ModUiModifiers modifiers = Tesseris.ModApi.ModUiModifiers.None;
        if ((e.Modifiers & KeyModifiers.Shift) != 0) modifiers |= Tesseris.ModApi.ModUiModifiers.Shift;
        if ((e.Modifiers & KeyModifiers.Control) != 0) modifiers |= Tesseris.ModApi.ModUiModifiers.Control;
        if ((e.Modifiers & KeyModifiers.Alt) != 0) modifiers |= Tesseris.ModApi.ModUiModifiers.Alt;
        if ((e.Modifiers & KeyModifiers.Super) != 0) modifiers |= Tesseris.ModApi.ModUiModifiers.Super;

        bool modal = HasActiveModScreen;
        _mods.Client.Input.DispatchKey(
            (int)e.Key,
            phase,
            modifiers,
            modal
                ? Tesseris.Game.Modding.Client.ModInputDispatchContext.Screen
                : Tesseris.Game.Modding.Client.ModInputDispatchContext.Gameplay,
            modal);
        if (!modal)
        {
            return;
        }

        DispatchModUiInput(new Tesseris.ModApi.ModUiInput(
            Tesseris.ModApi.ModUiInputKind.Key,
            phase,
            (int)MouseState.Position.X,
            (int)MouseState.Position.Y,
            KeyCode: (int)e.Key,
            Modifiers: modifiers));
    }

    /// <summary>
    /// Uloží všechny upravené chunky, které jsou zrovna v paměti.
    ///
    /// <para>Zápis se jen zařadí do fronty. Doopravdy ho dokončí <c>WorldStorage.Dispose</c>,
    /// který se volá až po zastavení workerů a co ve frontě zbylo, dopíše sám. Spoléhat
    /// tady na worker vlákna by nešlo — za chvíli se zastavují.</para>
    /// </summary>
    private void SaveLoadedChunks()
    {
        if (_storage is null || _world is null)
        {
            return;
        }

        int saved = 0;

        foreach (Vector3i position in _world.ChunkPositions)
        {
            if (_world.GetChunk(position) is { IsModified: true } chunk)
            {
                _storage.Save(position, chunk);
                saved++;
            }
        }

        Log.Info($"Ukládá se {saved} upravených chunků.");
    }

    /// <summary>Složka rozehraného světa. Prázdná v selftestu, který nic neukládá.</summary>
    private string? _worldDirectory;

    /// <summary>Kam se ukládá obsah pecí.</summary>
    private string FurnacePath => Path.Combine(_worldDirectory ?? string.Empty, "pece.dat");

    private string ChestPath => Path.Combine(_worldDirectory ?? string.Empty, Chests.FileName);

    private string InventoryPath => Path.Combine(_worldDirectory ?? string.Empty, "player-inventory.dat");

    /// <summary>Kam se ukládají ekologické hodiny a věk stromů.</summary>
    private string EcologyPath => Path.Combine(_worldDirectory ?? string.Empty, LivingVegetation.FileName);

    /// <summary>Kam se uklada spolecny graf venkovniho vedeni a povrchovych kabelu.</summary>
    private string PowerPath => Path.Combine(_worldDirectory ?? string.Empty, PowerNetwork.FileName);

    private string AnimalPath => Path.Combine(_worldDirectory ?? string.Empty, AnimalPopulation.FileName);

    /// <summary>Kde má svět uloženou kolonii.</summary>
    private string ColonyPath => Path.Combine(_worldDirectory ?? string.Empty, ColonySave.FileName);

    /// <summary>Za kolik tiků ohlásit, co načtená linka stihla. Nula znamená nehlásit.</summary>
    private int _loadedColonyWatch;

    /// <summary>Kolik kusů se načtené lince nasypalo na vstup.</summary>
    private int _loadedColonyFed;

    protected override void OnUnload()
    {
        SavePlayerOptions();

        // Mody dostanou poslední možnost zapsat svůj stav ještě před snapshotem světa. Jejich
        // assembly ale zůstávají načtené, dokud se nedokončí worker úlohy s worldgen hooky.
        _mods?.BeginShutdown();

        // Každý mod zapisuje vlastní atomický soubor. Save callback běží ještě s připojeným
        // světem, hráčem i inventářem, takže si mod může připravit poslední snapshot.
        _mods?.SaveWorld();

        // Všechno, co má hráč načtené kolem sebe, se při ukončení uloží. Bez tohohle by
        // se ukládalo jen to, co stihlo vypadnout z dohledu — tedy zrovna ne to, na čem
        // hráč naposledy pracoval.
        SaveLoadedChunks();

        // Pece až po chuncích, ale ještě před zastavením workerů: zapisuje to hlavní vlákno
        // rovnou na disk, takže na frontě úložiště nezáleží.
        if (_worldDirectory is not null)
        {
            _furnaces?.Save(FurnacePath);
            _chests?.Save(ChestPath);
            ColonySave.Save(_colony, ColonyPath);
            _ecology?.Save(EcologyPath, _saplings);
            _power?.Save(PowerPath);
            if (_animals is not null)
            {
                try
                {
                    _animals.Save(AnimalPath);
                }
                catch (IOException exception)
                {
                    Log.Warn($"Zvířata se nepodařilo uložit: {exception.Message}");
                }
            }
            if (_inventory is not null && _items is not null)
            {
                InventorySerializer.Save(InventoryPath, _inventory, _items);
            }
            if (_modRuntimeSave is not null && _modEntities is not null && _mods is not null)
            {
                _modRuntimeSave.Save(
                    _modEntities,
                    _mods.ItemStacks,
                    _mods.Containers,
                    _mods.SimulationClock.Tick);
            }
        }

        _mods?.CloseWorld();
        _modGame.DetachWorld();

        // Nejdřív zastavit workery, teprve pak uvolnit to, na co sahají.
        _jobs?.Dispose();
        _modAudioBridge?.Dispose();
        _modAudioBridge = null;
        _mods?.Dispose();
        _luaEntities?.Dispose();
        _luaEntities = null;
        _externalModLoaders?.Dispose();
        _audio?.Dispose();

        // Až po zastavení workerů: dokud běží, můžou do úložiště pořád zapisovat.
        _storage?.Dispose();

        _modRenderBridge?.Dispose();
        _modRenderBridge = null;

        _chunkRenderer?.Dispose();
        _sprites?.Dispose();
        _outline?.Dispose();
        _text?.Dispose();
        _textures?.Dispose();

        _renderer?.Dispose();

        base.OnUnload();

        if (_restartToMainMenuAfterUnload)
        {
            try
            {
                StartCurrentProcess();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                Log.Warn($"Návrat do hlavního menu se nepodařil: {exception.Message}");
            }
        }
    }

    private static void StartCurrentProcess()
    {
        string processPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("The current executable path is unavailable.");
        var start = new ProcessStartInfo(processPath)
        {
            UseShellExecute = true,
            WorkingDirectory = Environment.CurrentDirectory,
        };
        string[] currentArguments = Environment.GetCommandLineArgs();
        if (string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            start.ArgumentList.Add(currentArguments[0]);
        }
        foreach (string argument in currentArguments.Skip(1))
        {
            start.ArgumentList.Add(argument);
        }
        _ = Process.Start(start) ?? throw new InvalidOperationException("The restarted process did not start.");
    }

    /// <summary>
    /// Postaví vykreslování: zapne kontext, který k oknu založilo GLFW, a připraví cíle.
    /// </summary>
    /// <remarks>
    /// <para>Kontext sám vzniká už při zakládání okna — GLFW ho vyrábí podle
    /// <c>NativeWindowSettings</c>. Tady se jen udělá aktuálním pro tohle vlákno a přečte
    /// se, na čem se vlastně kreslí.</para>
    ///
    /// <para>Proti Vulkanu odpadl surface, swapchain i výběr zařízení. O obojí se stará
    /// ovladač a program o tom neví — což je zároveň důvod, proč se tady nedá vybrat
    /// grafika: bere se ta, kterou systém oknu přidělil.</para>
    /// </remarks>
    /// <summary>
    /// Postaví Vulkan: kontext, swapchain a smyčku snímku.
    ///
    /// Surface se nevyrábí ve Vulkanu, ale v GLFW — jen ono ví, jaké platformové rozšíření
    /// se má použít (na macOS <c>VK_EXT_metal_surface</c>). Kontext proto okno vůbec nezná
    /// a dostane jen seznam rozšíření a funkci, která surface vytvoří.
    /// </summary>
    private unsafe void InitializeRendering()
    {
        if (!GLFW.VulkanSupported())
        {
            throw new PlatformNotSupportedException(
                "GLFW nenašlo Vulkan. Na macOS je potřeba MoltenVK: brew install molten-vk vulkan-loader.");
        }

        var provider = new VulkanSurfaceProvider(
            GLFW.GetRequiredInstanceExtensions(),
            instance =>
            {
                int result = GLFW.CreateWindowSurface(
                    new GlfwVkHandle(instance), WindowPtr, null, out GlfwVkHandle surface);

                return result == 0 ? surface.Handle : 0;
            });

        _vulkan = VulkanContext.Create(provider, "Tesseris");
        _swapchain = new VulkanSwapchain(_vulkan, ClientSize.X, ClientSize.Y, _vsync);

        SelectAutomaticGraphicsProfile();

        _renderer = new VulkanRenderer(_vulkan, _swapchain)
        {
            // Obloha má přesně barvu mlhy, jinak by na obzoru vznikl ostrý předěl mezi
            // vzdáleným terénem a nebem.
            ClearColor = ChunkRenderer.SkyColor,
        };

        Log.Info($"Renderer: Vulkan / {_vulkan.DeviceName} ({_vulkan.ApiVersion}).");
    }

    private void SelectAutomaticGraphicsProfile()
    {
        double memoryGiB = (_vulkan?.DeviceLocalMemoryBytes ?? 0) / (1024.0 * 1024.0 * 1024.0);
        int detected = PlayerOptions.RecommendGraphicsProfile(
            (ulong)(_vulkan?.DeviceLocalMemoryBytes ?? 0),
            (_vulkan?.IsPortability ?? false));

        if (_playerOptions.AutomaticGraphics)
        {
            _playerOptions.ApplyGraphicsProfile(detected, automatic: true);
        }

        string profile = GraphicsProfileName(detected, StartupLanguage.English);
        string platform = (_vulkan?.IsPortability ?? false) ? "sdilena pamet" : "vlastni VRAM";
        _graphicsDetectionSummary = string.Create(
            CultureInfo.InvariantCulture,
            $"{(_vulkan is null ? "(nespusteno)" : $"{_vulkan.DeviceName}, Vulkan {_vulkan.ApiVersion}")}  |  {memoryGiB:0.0} GB {platform}  |  AUTO recommends {profile}");

        // rozdil hleda v shaderech misto v jednom cisle o velikosti pameti karty.
        Log.Info($"Graficky profil: {_graphicsDetectionSummary}.");

        try { _playerOptions.Save(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Automaticky graficky profil neslo ulozit: {exception.Message}");
        }
    }

    private static string GraphicsProfileName(int quality, StartupLanguage language)
    {
        bool english = language == StartupLanguage.English;
        return quality switch
        {
            PlayerOptions.LowGraphics => english ? "LOW" : "NIZKA",
            PlayerOptions.HighGraphics => english ? "HIGH" : "VYSOKA",
            _ => english ? "BALANCED" : "VYVAZENA",
        };
    }

    /// <summary>
    /// Změří meshing v ustáleném stavu na vlastním malém světě.
    ///
    /// .NET překládá metody nejdřív rychle a nekvalitně a na optimalizovanou verzi přepne až
    /// po několika desítkách volání; rozdíl je zhruba třicetinásobný. Časy z prvního průchodu
    /// tedy o rychlosti algoritmu neříkají nic.
    /// </summary>
    private void MeasureSteadyStateMeshing()
    {
        if (_registry is null || _generator is null)
        {
            return;
        }

        var sample = new VoxelWorld(_registry);
        var positions = new List<Vector3i>();

        for (int x = -1; x <= 1; x++)
        {
            for (int z = -1; z <= 1; z++)
            {
                for (int y = 0; y < TerrainGenerator.WorldHeightChunks; y++)
                {
                    var position = new Vector3i(x, y, z);
                    var chunk = new Chunk();
                    _generator.Generate(chunk, position);
                    sample.TryAddChunk(position, chunk);
                    positions.Add(position);
                }
            }
        }

        ushort[] padded = new ushort[ChunkMesher.PaddedVolume];
        byte[] pieces = new byte[ChunkMesher.PaddedVolume];
        ushort[] extraBlocks = new ushort[ChunkMesher.PaddedVolume];
        byte[] extraMasks = new byte[ChunkMesher.PaddedVolume];
        var opaque = new MeshBuffer();
        var transparent = new MeshBuffer();
        var stopwatch = new Stopwatch();

        // ZAHŘÍVÁ SE NA POČET VOLÁNÍ, NE NA POČET PRŮCHODŮ SVĚTEM.
        //
        // Dřív tu bylo 40 průchodů přes všechny chunky. Cena tím ale rostla s výškou světa,
        // a jak se výška zvedla na 32 chunků, dělalo to 11 520 meshů — přes minutu, tedy
        // víc, než kolik selftestu povoluje hlídač, a měření nikdy nedoběhlo.
        //
        // Na přepnutí do optimalizovaného kódu stačí několik desítek volání téže metody;
        // pár set je pohodlná rezerva a na výšce světa nezávisí.
        // Strop je na obojím: na počtu volání i na čase. Počet proto, aby .NET stihl přepnout
        // na optimalizovaný kód (děje se to na pozadí, takže pár desítek volání nestačí —
        // naměřeno: po 400 voláních vycházel nejhorší chunk 14,7 ms, po 3000 zlomek toho).
        // Čas proto, aby zahřívání nerostlo s výškou světa a vešlo se do hlídače selftestu.
        const int WarmupBuilds = 3000;
        TimeSpan warmupLimit = TimeSpan.FromSeconds(6);

        var warmup = Stopwatch.StartNew();
        for (int build = 0; build < WarmupBuilds && warmup.Elapsed < warmupLimit; build++)
        {
            Vector3i position = positions[build % positions.Count];
            sample.CopyPadded(position, padded, pieces);
            ChunkMesher.Build(padded, _registry, opaque, transparent, position, pieces: pieces);
        }

        double worst = 0.0;
        foreach (Vector3i position in positions)
        {
            sample.CopyPadded(position, padded, pieces);

            stopwatch.Restart();
            ChunkMesher.Build(padded, _registry, opaque, transparent, position, pieces: pieces);
            stopwatch.Stop();

            worst = Math.Max(worst, stopwatch.Elapsed.TotalMilliseconds);
        }

        MeshSteadyStateWorstMs = worst;
        Log.Info($"Meshing v ustáleném stavu: nejpomalejší z {positions.Count} chunků {worst:F3} ms.");
    }

    private void AttributePreviousFrameToGc()
    {
        if (_frameTimer.FrameCount <= 100)
        {
            _lastGen2Count = GC.CollectionCount(2);
            return;
        }

        int gen2 = GC.CollectionCount(2);

        if (gen2 != _lastGen2Count)
        {
            WorstFrameWithGen2 = Math.Max(WorstFrameWithGen2, _frameTimer.LastTotalMs);
        }
        else
        {
            WorstFrameWithoutGen2 = Math.Max(WorstFrameWithoutGen2, _frameTimer.LastTotalMs);
        }

        _lastGen2Count = gen2;
    }

    /// <summary>
    /// Rozvrh selftestu. Většinu času se prolétá svět, aby se streaming držel v chodu.
    /// Konec patří kontrolám:
    ///
    ///   N-40: rozbití bloků a zápis počtu trojúhelníků před změnou
    ///   N-10: zápis počtu po změně — musí se lišit, jinak se přemeshování neprojevilo
    ///   N-4:  kamera otočená od světa   → kolik chunků projde frustum cullingem
    ///   N-3:  kamera zpět
    ///   N-2:  vypnutý backface culling  → snímek pro srovnání
    ///   N-1:  zapnutý backface culling  → snímek, overlay, raycast
    /// </summary>
    /// <summary>
    /// Zvuková zkouška, první část: zapamatuje si počet hrajících hlasů a zahraje poziční
    /// zvuk stranou od posluchače.
    ///
    /// <para>Bez téhle zkoušky by se o zvuku dalo tvrdit jen to, že se engine nastartoval —
    /// což neříká nic o tom, jestli něco opravdu hraje. Nedokazuje to slyšitelnost, tu
    /// neověří nic než ucho; dokazuje to, že SoLoud zvuk přijal a míchá ho.</para>
    /// </summary>
    private void StartAudioProbe()
    {
        if (_audio is null || _sounds is null || !_audio.IsAvailable)
        {
            AudioProbe = "zvukový engine neběží, zkouška přeskočena";
            return;
        }

        _voicesBeforeProbe = _audio.ActiveVoices;

        // Osm bloků stranou, aby zvuk nevycházel z hlavy posluchače — to je přesně to,
        // co má poziční zvuk umět a co se u něj dá splést.
        _probeSoundBlock = new Vector3i(
            (int)_camera.Position.X + 8, (int)_camera.Position.Y, (int)_camera.Position.Z);

        _sounds.Play(BlockMaterial.Stone, BlockAction.Chisel, _probeSoundBlock);
    }

    /// <summary>Zvuková zkouška, druhá část: o frame později se podívá, jestli hlas přibyl.</summary>
    private void FinishAudioProbe()
    {
        if (_audio is null || !_audio.IsAvailable)
        {
            return;
        }

        int after = _audio.ActiveVoices;
        float distance = (BlockSounds.BlockCenter(_probeSoundBlock) - _camera.Position).Length;

        AudioProbePassed = after > _voicesBeforeProbe;
        AudioProbe = AudioProbePassed
            ? $"hlasů {_voicesBeforeProbe} -> {after}, zvuk zní {distance:F1} bloku od posluchače"
            : $"hlas nepřibyl (před {_voicesBeforeProbe}, po {after})";
    }

    private void RunSelftestSchedule()
    {
        if (_chunkRenderer is null)
        {
            return;
        }

        long frame = _frameTimer.FrameCount;

        // POZOR NA else-if: sonda velitele stála za větví letu, která platí skoro celý běh,
        // takže se nikdy nespustila a hlásila blend 0. Musí stát samostatně.
        if (frame < _selftestFrames - 40)
        {
            float distance = MathF.Min(SelftestFlightSpeed * (float)_frameTimer.DeltaSeconds, 1.5f);

            Vector3 direction = _camera.Forward;
            direction.Y = 0f;

            if (direction.LengthSquared > 1e-6f)
            {
                Vector3 position = _player.Position + (Vector3.Normalize(direction) * distance);

                // Let musí kopírovat terén. Posun byl původně jen vodorovný a fungoval jen
                // proto, že starý generátor dělal skoro placku — jakmile terén dostal
                // kontinenty a hory, hráč zaletěl dovnitř kopce. Poznalo se to na tom, že
                // paprsek vracel vzdálenost 0 a nulovou normálu (start uvnitř bloku)
                // a rozdíl culling on/off vyskočil na 24 %, protože kamera koukala na
                // odvrácené stěny zevnitř geometrie.
                float ground = _generator?.SurfaceHeight((int)MathF.Floor(position.X), (int)MathF.Floor(position.Z))
                               ?? 0;
                position.Y = ground + SelftestFlightClearance;

                _player.Position = position;
                FlightDistance += distance;
            }
        }
        if (_commanderProbe && frame == 200)
        {
            // PŘEPÍNÁ SE HNED, ne až na konci. Přechod trvá 0,45 s, ale ke konci běhu jde
            // snímek za 1,6 ms — se čtyřiceti snímky došel blend na 0,52, se dvěma sty na
            // 0,70, a kolmý pohled, tedy přesně ten degenerovaný případ, se nevyzkoušel ani
            // jednou. Sonda, která nedojede na doraz, netestuje nic. Měření tím netrpí:
            // tahle sonda má vlastní proměnnou a v běžném selftestu se nezapíná.
            ToggleCommanderMode();
        }

        if (frame == _selftestFrames - 40)
        {
            RunEditProbe();
            RunChiselProbe();

            // REŽIM VELITELE V BĚŽÍCÍM ENGINU. Selftest do něj nikdy nevstupoval, a proto
            // v něm mohl roky sedět pád na NaN matici — kolmý pohled dolů dělá z osy
            // vektorový součin nulového vektoru. Ověřuje se až na konci běhu, aby
            // velitelská kamera nezasáhla do měření streamingu a meshingu.
        }
        else if (_commanderProbe && frame == _selftestFrames - 3)
        {
            Matrix4 viewProjection = _camera.ViewMatrix * _camera.ProjectionMatrix;
            bool finite = true;
            for (int row = 0; row < 4 && finite; row++)
            {
                for (int column = 0; column < 4; column++)
                {
                    finite &= float.IsFinite(viewProjection[row, column]);
                }
            }

            Log.Info($"SONDA VELITELE: blend {_commander.Blend:F3}, smer {_camera.ViewForward}, "
                + $"right {_camera.ViewRight}, view-projection konecna: {finite}, "
                + $"ruka skryta: {_chunkRenderer!.Held is null}, "
                + $"kurzor: {CursorState}");
        }
        else if (frame == _selftestFrames - 12)
        {
            StartAudioProbe();
        }
        else if (frame == _selftestFrames - 11)
        {
            FinishAudioProbe();
        }
        else if (frame == _selftestFrames - 10)
        {
            TrianglesAfterEdit = _chunkRenderer.TriangleCount;
            RecordMicroShapeCount();
        }
        else if (frame == _selftestFrames - 4)
        {
            _camera.ApplyLook(180f, 0f);
        }
        else if (frame == _selftestFrames - 3)
        {
            VisibleChunksLookingAway = _chunkRenderer.VisibleChunks;
            _camera.ApplyLook(180f, 0f);
        }

        _chunkRenderer.BackfaceCulling = frame != _selftestFrames - 2;
    }

    /// <summary>
    /// Vyhloubí pod hráčem díru a zapíše, kolik trojúhelníků měla scéna předtím.
    ///
    /// Kdyby se přemeshování po úpravě neprovedlo, počet trojúhelníků by zůstal stejný
    /// a změna by na obrazovce nebyla vidět, i kdyby data ve světě byla správně.
    /// </summary>
    private void RunEditProbe()
    {
        if (_world is null || _streamer is null || _chunkRenderer is null)
        {
            return;
        }

        TrianglesBeforeEdit = _chunkRenderer.TriangleCount;

        int centerX = (int)MathF.Floor(_player.Position.X);
        int centerZ = (int)MathF.Floor(_player.Position.Z);
        int removed = 0;

        // Mělká jáma v povrchu. Záměrně malá: každá úprava vyrobí kopii chunku, takže
        // hrubá díra přes celou výšku světa by z měření paměti udělala nesmysl a přitom
        // by nedokázala o nic víc. Šířka přes hranici chunku stačí na ověření, že se
        // zneplatní i sousedé.
        for (int x = centerX - 5; x <= centerX + 5; x++)
        {
            for (int z = centerZ - 5; z <= centerZ + 5; z++)
            {
                int surface = _generator?.SurfaceHeight(x, z) ?? 0;

                for (int y = surface; y > surface - 6 && y > 0; y--)
                {
                    if (_world.GetBlock(x, y, z) == BlockRegistry.Air)
                    {
                        continue;
                    }

                    _world.SetBlock(x, y, z, BlockRegistry.Air);
                    _streamer.InvalidateBlock(x, y, z);
                    removed++;
                }
            }
        }

        EditProbe = string.Create(
            CultureInfo.InvariantCulture,
            $"odebráno {removed} bloků kolem [{centerX}, {centerZ}]");
    }

    /// <summary>
    /// Vyteše tisíc bloků do stejného tvaru a ověří tři věci najednou:
    ///
    ///   1. deduplikaci — tisíc stejně otesaných bloků musí sdílet jeden tvar,
    ///   2. dvouúrovňový paprsek — musí trefit konkrétní mikrovoxel, ne jen blok,
    ///   3. mikro kolizi — místo, ze kterého se ubralo, musí přestat překážet.
    /// </summary>
    private void RunChiselProbe()
    {
        if (_world is null || _streamer is null || _generator is null)
        {
            return;
        }

        int baseX = (int)MathF.Floor(_player.Position.X) + 20;
        int baseZ = (int)MathF.Floor(_player.Position.Z);

        var tool = new ChiselTool { Mode = ChiselMode.Remove, Size = 4 };
        int chiselled = 0;

        // Postaví se sloupky a každý se otese úplně stejně. Kdyby se tvary nesdílely,
        // vznikne tisíc samostatných mesh místo jedné.
        for (int i = 0; i < 1000; i++)
        {
            int x = baseX + (i % 40);
            int z = baseZ + (i / 40);
            int y = _generator.SurfaceHeight(x, z) + 1;

            _world.SetBlock(x, y, z, _world.Registry.IndexOf("tesseris:stone"));

            var hit = new MicroHit(
                new Vector3i(x, y, z),
                new Vector3i(0, 12, 0),
                new Vector3i(0, 1, 0),
                BlockFace.PosY,
                0f,
                Vector3.Zero,
                _world.Registry.IndexOf("tesseris:stone"));

            if (tool.Apply(_world, hit, out Vector3i affected))
            {
                _streamer.InvalidateBlock(affected.X, affected.Y, affected.Z);
                chiselled++;
            }
        }

        ChiselledBlocks = chiselled;

        // Kontrola paprsku: musí najít mikrovoxel, ne jen blok.
        int probeX = baseX;
        int probeZ = baseZ;
        int probeY = _generator.SurfaceHeight(probeX, probeZ) + 1;

        var origin = new Vector3(probeX + 0.5f, probeY + 6f, probeZ + 0.5f);
        bool rayFound = MicroRaycast.Cast(_world, origin, -Vector3.UnitY, 20f, out MicroHit rayHit);
        bool rayIsMicro = rayFound && rayHit.IsMicro && rayHit.Block == new Vector3i(probeX, probeY, probeZ);

        // Kontrola kolize: v odtesané prohlubni musí být volno, pod ní ne.
        var carved = new Aabb(
            new Vector3(probeX + 0.1f, probeY + 0.85f, probeZ + 0.1f),
            new Vector3(probeX + 0.15f, probeY + 0.95f, probeZ + 0.15f));

        var below = new Aabb(
            new Vector3(probeX + 0.4f, probeY + 0.1f, probeZ + 0.4f),
            new Vector3(probeX + 0.6f, probeY + 0.3f, probeZ + 0.6f));

        bool carvedFree = PlayerController.IsFree(_world, carved);
        bool belowBlocked = !PlayerController.IsFree(_world, below);

        MicroChecksPassed = rayIsMicro && carvedFree && belowBlocked;

        ChiselProbe = string.Create(
            CultureInfo.InvariantCulture,
            $"otesáno {chiselled} bloků, " +
            $"paprsek {(rayIsMicro ? "trefil mikrovoxel" : "selhal")}, " +
            $"díra {(carvedFree ? "volná" : "CHYBA")}, hmota pod ní {(belowBlocked ? "drží" : "CHYBA")}");
    }

    /// <summary>
    /// Přečte počet různých otesaných tvarů.
    ///
    /// Musí se to udělat až po tom, co worker vlákna stihla chunky přemeshovat — zásoba
    /// tvarů se plní právě tam. Čtení hned po otesání by vrátilo nulu a kritérium
    /// „tisíc bloků sdílí pár tvarů" by prošlo, aniž by se cokoli sdílelo.
    /// </summary>
    private void RecordMicroShapeCount()
    {
        if (_world is null)
        {
            return;
        }

        UniqueMicroShapes = _world.MicroShapes.UniqueShapes;

        Log.Info(
            $"Zásoba otesaných tvarů: {UniqueMicroShapes} různých, " +
            $"{_world.MicroShapes.Hits} zásahů, {_world.MicroShapes.Misses} výpočtů.");
    }

    /// <summary>Vykreslí jednoduché úvodní menu nebo průběh pregenerace.</summary>
    private void DrawPauseMenu()
    {
        if (_sprites is null || _text is null) return;

        PauseLayout layout = PauseLayoutFor(ClientSize.X, ClientSize.Y);
        Vector4 dim = new(0f, 0f, 0f, 0.62f);
        Vector4 panel = new(0.055f, 0.065f, 0.085f, 0.98f);
        Vector4 edge = new(0.28f, 0.48f, 0.62f, 1f);
        Vector4 button = new(0.12f, 0.16f, 0.21f, 1f);
        Vector4 accent = new(0.18f, 0.38f, 0.48f, 1f);

        _sprites.Begin(ClientSize.X, ClientSize.Y);
        _sprites.DrawRect(0f, 0f, ClientSize.X, ClientSize.Y, dim);
        _sprites.DrawRect(layout.Panel.X - 3f, layout.Panel.Y - 3f, layout.Panel.Width + 6f, layout.Panel.Height + 6f, edge);
        _sprites.DrawRect(layout.Panel.X, layout.Panel.Y, layout.Panel.Width, layout.Panel.Height, panel);

        if (!_optionsMenuOpen)
        {
            foreach (UiBox box in new[] { layout.Resume, layout.Options, layout.SaveAndQuit })
            {
                _sprites.DrawRect(box.X, box.Y, box.Width, box.Height, button);
            }
        }
        else
        {
            for (int row = 0; row < 7; row++)
            {
                UiBox minus = OptionMinus(layout, row);
                UiBox plus = OptionPlus(layout, row);
                _sprites.DrawRect(minus.X, minus.Y, minus.Width, minus.Height, accent);
                _sprites.DrawRect(plus.X, plus.Y, plus.Width, plus.Height, accent);
            }
            _sprites.DrawRect(layout.Back.X, layout.Back.Y, layout.Back.Width, layout.Back.Height, button);
        }
        _sprites.End();

        bool english = UiLanguage == StartupLanguage.English;
        _text.Begin(ClientSize.X, ClientSize.Y);
        _text.DrawText(english ? "GAME PAUSED" : "HRA POZASTAVENA", (int)layout.Panel.X + 42, (int)layout.Panel.Y + 38, 3);
        if (!_optionsMenuOpen)
        {
            _text.DrawText(english ? "RESUME" : "POKRACOVAT", (int)layout.Resume.X + 24, (int)layout.Resume.Y + 14, 2);
            _text.DrawText(english ? "OPTIONS" : "NASTAVENI", (int)layout.Options.X + 24, (int)layout.Options.Y + 14, 2);
            _text.DrawText(english ? "SAVE AND MAIN MENU" : "ULOZIT A HLAVNI MENU", (int)layout.SaveAndQuit.X + 18, (int)layout.SaveAndQuit.Y + 14, 2);
            _text.DrawText(english ? "Esc resumes the game" : "Esc pokracuje ve hre", (int)layout.Panel.X + 70, (int)layout.Panel.Y + (int)layout.Panel.Height - 62, 1);
        }
        else
        {
            string[] labels = english
                ? ["Mouse sensitivity", "Field of view", "View distance", "Master volume", "VSync", "Full View", "Graphics quality"]
                : ["Citlivost mysi", "Zorne pole", "Dohled", "Hlasitost", "VSync", "Full View", "Kvalita grafiky"];
            string qualityName = _playerOptions.AutomaticGraphics
                ? "AUTO"
                : _playerOptions.CustomGraphics
                    ? "CUSTOM"
                    : GraphicsProfileName(_playerOptions.GraphicsQuality, UiLanguage);
            string[] values =
            [
                $"{_mouseSensitivity:0.000}",
                $"{_camera.FieldOfViewDegrees:0}°",
                $"{_streamer?.ViewDistanceChunks ?? 0} ch",
                $"{_playerOptions.MasterVolume * 100f:0}%",
                _vsync ? "ON" : "OFF",
                _playerOptions.FullView ? "ON" : "OFF",
                qualityName,
            ];
            for (int row = 0; row < labels.Length; row++)
            {
                UiBox minus = OptionMinus(layout, row);
                UiBox plus = OptionPlus(layout, row);
                _text.DrawText("-", (int)minus.X + 15, (int)minus.Y + 9, 2);
                _text.DrawText("+", (int)plus.X + 13, (int)plus.Y + 9, 2);
                _text.DrawText(labels[row], (int)minus.X + 64, (int)minus.Y + 4, 1);
                _text.DrawText(values[row], (int)minus.X + 64, (int)minus.Y + 22, 1);
            }
            _text.DrawText(english ? "BACK" : "ZPET", (int)layout.Back.X + 24, (int)layout.Back.Y + 11, 2);
        }
        _text.End(_stats);
    }

    private void DrawStartupScreen()
    {
        if (_sprites is null || _text is null)
        {
            return;
        }

        StartupLayout layout = StartupLayoutFor(ClientSize.X, ClientSize.Y);
        Vector2 pointer = MouseState.Position;

        var veil = new Vector4(0.018f, 0.025f, 0.028f, 0.94f);
        var panel = new Vector4(0.055f, 0.071f, 0.071f, 0.98f);
        var edge = new Vector4(0.25f, 0.72f, 0.43f, 1f);
        var field = new Vector4(0.09f, 0.115f, 0.11f, 1f);
        var fieldFocused = new Vector4(0.12f, 0.18f, 0.145f, 1f);
        var button = new Vector4(0.12f, 0.43f, 0.25f, 1f);
        var buttonHover = new Vector4(0.16f, 0.57f, 0.32f, 1f);
        var quiet = new Vector4(0.08f, 0.105f, 0.105f, 1f);

        _sprites.Begin(ClientSize.X, ClientSize.Y);
        _sprites.DrawRect(0f, 0f, ClientSize.X, ClientSize.Y, veil);
        _sprites.DrawRect(layout.Panel.X, layout.Panel.Y, layout.Panel.Width, layout.Panel.Height, panel);
        _sprites.DrawRect(layout.Panel.X, layout.Panel.Y, 6f, layout.Panel.Height, edge);

        if (_startupStage == StartupStage.MainMenu && _mainMenu is not null && _graphicsSettingsOpen)
        {
            DrawGraphicsSettingsSprites(layout, pointer, edge, field, button, buttonHover, quiet);
        }
        else if (_startupStage == StartupStage.MainMenu && _mainMenu is not null && _modManagerOpen)
        {
            DrawModManagerSprites(layout, pointer, edge, field, button, buttonHover, quiet);
        }
        else if (_startupStage == StartupStage.MainMenu && _mainMenu is not null)
        {
            bool czech = _mainMenu.Language == StartupLanguage.Czech;
            Vector4 selectedLanguage = new Vector4(0.20f, 0.62f, 0.34f, 1f);

            _sprites.DrawRect(
                layout.LanguageCzech.X, layout.LanguageCzech.Y,
                layout.LanguageCzech.Width, layout.LanguageCzech.Height,
                czech ? selectedLanguage : field);
            _sprites.DrawRect(
                layout.LanguageEnglish.X, layout.LanguageEnglish.Y,
                layout.LanguageEnglish.Width, layout.LanguageEnglish.Height,
                czech ? field : selectedLanguage);
            _sprites.DrawFrame(layout.LanguageCzech.X, layout.LanguageCzech.Y, layout.LanguageCzech.Width, layout.LanguageCzech.Height, 1f, edge);
            _sprites.DrawFrame(layout.LanguageEnglish.X, layout.LanguageEnglish.Y, layout.LanguageEnglish.Width, layout.LanguageEnglish.Height, 1f, edge);
            _sprites.DrawRect(layout.Info.X, layout.Info.Y, layout.Info.Width, layout.Info.Height, quiet);
            _sprites.DrawFrame(layout.Info.X, layout.Info.Y, layout.Info.Width, layout.Info.Height, 2f, edge);

            Vector4 continueColor = _continueWorld is null
                ? quiet
                : (layout.Continue.Contains(pointer) ? buttonHover : button);
            Vector4 leftWorldColor = _knownWorlds.Count <= 1
                ? quiet
                : (layout.WorldLeft.Contains(pointer) ? buttonHover : field);
            Vector4 rightWorldColor = _knownWorlds.Count <= 1
                ? quiet
                : (layout.WorldRight.Contains(pointer) ? buttonHover : field);

            _sprites.DrawRect(
                layout.WorldLeft.X, layout.WorldLeft.Y,
                layout.WorldLeft.Width, layout.WorldLeft.Height,
                leftWorldColor);
            _sprites.DrawRect(
                layout.Continue.X, layout.Continue.Y,
                layout.Continue.Width, layout.Continue.Height,
                continueColor);
            _sprites.DrawRect(
                layout.WorldRight.X, layout.WorldRight.Y,
                layout.WorldRight.Width, layout.WorldRight.Height,
                rightWorldColor);

            _sprites.DrawRect(
                layout.Name.X, layout.Name.Y, layout.Name.Width, layout.Name.Height,
                _mainMenu.FocusedField == MainMenuField.Name ? fieldFocused : field);
            _sprites.DrawFrame(
                layout.Name.X, layout.Name.Y, layout.Name.Width, layout.Name.Height, 2f,
                _mainMenu.FocusedField == MainMenuField.Name ? edge : quiet);

            _sprites.DrawRect(
                layout.Seed.X, layout.Seed.Y, layout.Seed.Width, layout.Seed.Height,
                _mainMenu.FocusedField == MainMenuField.Seed ? fieldFocused : field);
            _sprites.DrawFrame(
                layout.Seed.X, layout.Seed.Y, layout.Seed.Width, layout.Seed.Height, 2f,
                _mainMenu.FocusedField == MainMenuField.Seed ? edge : quiet);

            Vector4 presetArrow = _worldPresets.Count <= 1 ? quiet : field;
            _sprites.DrawRect(
                layout.PresetLeft.X, layout.PresetLeft.Y,
                layout.PresetLeft.Width, layout.PresetLeft.Height,
                presetArrow);
            _sprites.DrawRect(
                layout.Preset.X, layout.Preset.Y,
                layout.Preset.Width, layout.Preset.Height,
                field);
            _sprites.DrawRect(
                layout.PresetRight.X, layout.PresetRight.Y,
                layout.PresetRight.Width, layout.PresetRight.Height,
                presetArrow);

            _sprites.DrawRect(
                layout.Pregen.X, layout.Pregen.Y,
                layout.Pregen.Width, layout.Pregen.Height,
                field);

            _sprites.DrawRect(
                layout.Create.X, layout.Create.Y, layout.Create.Width, layout.Create.Height,
                layout.Create.Contains(pointer) ? buttonHover : button);
            _sprites.DrawRect(
                layout.Mods.X, layout.Mods.Y, layout.Mods.Width, layout.Mods.Height,
                layout.Mods.Contains(pointer) ? buttonHover : button);
            _sprites.DrawRect(
                layout.Discord.X, layout.Discord.Y, layout.Discord.Width, layout.Discord.Height,
                layout.Discord.Contains(pointer) ? buttonHover : field);
            _sprites.DrawRect(
                layout.Patreon.X, layout.Patreon.Y, layout.Patreon.Width, layout.Patreon.Height,
                layout.Patreon.Contains(pointer) ? buttonHover : field);
            _sprites.DrawRect(
                layout.CreateMod.X, layout.CreateMod.Y, layout.CreateMod.Width, layout.CreateMod.Height,
                layout.CreateMod.Contains(pointer) ? buttonHover : field);
            _sprites.DrawRect(
                layout.Settings.X, layout.Settings.Y, layout.Settings.Width, layout.Settings.Height,
                layout.Settings.Contains(pointer) ? buttonHover : field);
        }
        else
        {
            int remaining = (_streamer?.PendingGeneration ?? 0) + (_streamer?.GeneratingCount ?? 0);
            float progress = _preparationTotal <= 0
                ? 0f
                : Math.Clamp((_preparationTotal - remaining) / (float)_preparationTotal, 0f, 1f);

            var bar = new UiBox(
                layout.Panel.X + 64f,
                layout.Panel.Y + 305f,
                layout.Panel.Width - 128f,
                28f);

            _sprites.DrawRect(bar.X, bar.Y, bar.Width, bar.Height, quiet);
            _sprites.DrawRect(
                bar.X + 3f,
                bar.Y + 3f,
                (bar.Width - 6f) * (string.IsNullOrWhiteSpace(_startupError) ? progress : 1f),
                bar.Height - 6f,
                string.IsNullOrWhiteSpace(_startupError)
                    ? edge
                    : new Vector4(0.72f, 0.16f, 0.14f, 1f));
        }

        _sprites.End();

        _text.Begin(ClientSize.X, ClientSize.Y);

        if (!_modManagerOpen && !_graphicsSettingsOpen)
        {
            DrawCenteredText("TESSERIS", layout.Panel, (int)layout.Panel.Y + 34, scale: 4);
        }

        if (_startupStage == StartupStage.MainMenu && _mainMenu is not null && _graphicsSettingsOpen)
        {
            DrawGraphicsSettingsText(layout);
        }
        else if (_startupStage == StartupStage.MainMenu && _mainMenu is not null && _modManagerOpen)
        {
            DrawModManagerText(layout);
        }
        else if (_startupStage == StartupStage.MainMenu && _mainMenu is not null)
        {
            StartupLocalization.Copy copy = StartupLocalization.For(_mainMenu.Language);
            string continueText = _continueWorld is null
                ? copy.NoSavedWorld
                : ShortUiLine($"{copy.Play}: {_continueWorld.DisplayName}", 28);

            DrawCenteredText("CESKY", layout.LanguageCzech, (int)layout.LanguageCzech.Y + 8, 1);
            DrawCenteredText("ENGLISH", layout.LanguageEnglish, (int)layout.LanguageEnglish.Y + 8, 1);

            _text.DrawText(
                _knownWorlds.Count == 0
                    ? copy.SavedWorlds
                    : $"{copy.SavedWorlds}  {_continueWorldIndex + 1} / {_knownWorlds.Count}",
                (int)layout.WorldLeft.X,
                (int)layout.Continue.Y - 24,
                2);
            DrawCenteredText("<", layout.WorldLeft, (int)layout.WorldLeft.Y + 13, scale: 2);
            DrawCenteredText(continueText, layout.Continue, (int)layout.Continue.Y + 13, scale: 2);
            DrawCenteredText(">", layout.WorldRight, (int)layout.WorldRight.Y + 13, scale: 2);

            _text.DrawText(copy.NewWorld, (int)layout.Name.X, (int)layout.Panel.Y + 202, 3);
            _text.DrawText(copy.Name, (int)layout.Name.X, (int)layout.Name.Y - 24, 2);
            _text.DrawText(
                _mainMenu.NameText + (_mainMenu.FocusedField == MainMenuField.Name ? "_" : string.Empty),
                (int)layout.Name.X + 12,
                (int)layout.Name.Y + 12,
                2);

            _text.DrawText($"{copy.Seed}  ({copy.SeedHint})", (int)layout.Seed.X, (int)layout.Seed.Y - 24, 2);
            _text.DrawText(
                _mainMenu.SeedText + (_mainMenu.FocusedField == MainMenuField.Seed ? "_" : string.Empty),
                (int)layout.Seed.X + 12,
                (int)layout.Seed.Y + 12,
                2);

            Tesseris.ModApi.ModWorldPresetDefinition? selectedPreset = _worldPresets.Count == 0
                ? null
                : _worldPresets[Math.Clamp(_worldPresetIndex, 0, _worldPresets.Count - 1)];
            _text.DrawText(copy.WorldPreset, (int)layout.Preset.X, (int)layout.Preset.Y - 24, 2);
            DrawCenteredText("<", layout.PresetLeft, (int)layout.PresetLeft.Y + 11, 2);
            DrawCenteredText(
                selectedPreset?.DisplayName ?? VanillaWorldPresetFactory.PresetId.Value,
                layout.Preset,
                (int)layout.Preset.Y + 11,
                1);
            DrawCenteredText(">", layout.PresetRight, (int)layout.PresetRight.Y + 11, 2);

            _text.DrawText(copy.AutomaticPregen, (int)layout.Pregen.X, (int)layout.Pregen.Y - 24, 2);
            DrawCenteredText(
                string.Format(CultureInfo.InvariantCulture, copy.FullViewFormat, MainMenu.AutomaticPregenRadius),
                layout.Pregen,
                (int)layout.Pregen.Y + 11,
                2);

            DrawCenteredText(copy.CreateWorld, layout.Create, (int)layout.Create.Y + 15, 2);

            int enabledMods = _modManager?.Mods.Count(mod => mod.Enabled) ?? 0;
            int installedMods = _modManager?.Mods.Count ?? 0;
            string modsLabel = _mainMenu.Language == StartupLanguage.Czech
                ? $"MODY  {enabledMods} / {installedMods}"
                : $"MODS  {enabledMods} / {installedMods}";
            int communityScale = layout.Mods.Width >= 150f ? 2 : 1;
            string compactModsLabel = communityScale == 2
                ? modsLabel
                : (_mainMenu.Language == StartupLanguage.Czech ? "MODY" : "MODS");
            DrawCenteredText(
                compactModsLabel,
                layout.Mods,
                (int)layout.Mods.Y + (communityScale == 2 ? 11 : 16),
                communityScale);
            DrawCenteredText(
                "DISCORD",
                layout.Discord,
                (int)layout.Discord.Y + (communityScale == 2 ? 11 : 16),
                communityScale);
            DrawCenteredText(
                "PATREON",
                layout.Patreon,
                (int)layout.Patreon.Y + (communityScale == 2 ? 11 : 16),
                communityScale);
            DrawCenteredText(
                "CREATE MOD",
                layout.CreateMod,
                (int)layout.CreateMod.Y + (communityScale == 2 ? 11 : 16),
                communityScale);
            DrawCenteredText(
                _mainMenu.Language == StartupLanguage.Czech ? "GRAFIKA" : "GRAPHICS",
                layout.Settings,
                (int)layout.Settings.Y + (communityScale == 2 ? 11 : 16),
                communityScale);

            if (!string.IsNullOrWhiteSpace(_startupError))
            {
                _text.DrawText(ShortUiLine(_startupError, 54), (int)layout.Create.X, (int)layout.Create.Y + 65, 1);
            }
            else
            {
                _text.DrawText(
                    copy.PregenHint,
                    (int)layout.Create.X,
                    (int)layout.Create.Y + 65,
                    1);
            }

            DrawStartupInfo(copy, layout.Info);
        }
        else
        {
            StartupLocalization.Copy copy = StartupLocalization.For(
                _mainMenu?.Language ?? StartupLanguage.Czech);

            if (!string.IsNullOrWhiteSpace(_startupError))
            {
                DrawCenteredText(copy.PreparationFailed, layout.Panel, (int)layout.Panel.Y + 158, 3);
                DrawCenteredText(
                    ShortUiLine(_startupError, 54),
                    layout.Panel,
                    (int)layout.Panel.Y + 242,
                    1);
                DrawCenteredText(
                    copy.PreparationErrorHint,
                    layout.Panel,
                    (int)layout.Panel.Y + 350,
                    1);
            }
            else
            {
                int remaining = (_streamer?.PendingGeneration ?? 0) + (_streamer?.GeneratingCount ?? 0);
                int done = Math.Clamp(_preparationTotal - remaining, 0, Math.Max(_preparationTotal, 0));
                int percent = _preparationTotal <= 0 ? 0 : (int)MathF.Round(done * 100f / _preparationTotal);

                DrawCenteredText(copy.PreparingWorld, layout.Panel, (int)layout.Panel.Y + 158, 3);
                DrawCenteredText(
                    remaining == 0
                        ? copy.FinalizingGeometry
                        : string.Format(
                            CultureInfo.InvariantCulture,
                            copy.ChunksFormat,
                            done.ToString(CultureInfo.InvariantCulture),
                            _preparationTotal.ToString(CultureInfo.InvariantCulture)),
                    layout.Panel,
                    (int)layout.Panel.Y + 242,
                    2);
                DrawCenteredText($"{percent} %", layout.Panel, (int)layout.Panel.Y + 348, 2);
                DrawCenteredText(
                    copy.PreparationHint,
                    layout.Panel,
                    (int)layout.Panel.Y + 414,
                    1);
            }
        }

        _text.End(_stats);
    }

    private void DrawGraphicsSettingsSprites(
        StartupLayout startup,
        Vector2 pointer,
        Vector4 edge,
        Vector4 field,
        Vector4 button,
        Vector4 buttonHover,
        Vector4 quiet)
    {
        GraphicsSettingsLayout layout = GraphicsSettingsLayoutFor(startup);
        _sprites!.DrawRect(
            layout.Back.X, layout.Back.Y, layout.Back.Width, layout.Back.Height,
            layout.Back.Contains(pointer) ? buttonHover : button);

        for (int row = 0; row < layout.Minus.Length; row++)
        {
            UiBox minus = layout.Minus[row];
            UiBox plus = layout.Plus[row];
            _sprites.DrawRect(minus.X, minus.Y, minus.Width, minus.Height,
                minus.Contains(pointer) ? buttonHover : field);
            _sprites.DrawRect(plus.X, plus.Y, plus.Width, plus.Height,
                plus.Contains(pointer) ? buttonHover : field);

            float valueX = minus.X + 58f;
            float valueWidth = plus.X - valueX - 10f;
            _sprites.DrawRect(valueX, minus.Y, valueWidth, minus.Height,
                row == _graphicsSettingsRow ? new Vector4(0.11f, 0.20f, 0.15f, 1f) : quiet);
            if (row == _graphicsSettingsRow)
            {
                _sprites.DrawFrame(valueX, minus.Y, valueWidth, minus.Height, 2f, edge);
            }
        }
    }

    private void DrawGraphicsSettingsText(StartupLayout startup)
    {
        if (_text is null || _mainMenu is null) return;
        GraphicsSettingsLayout layout = GraphicsSettingsLayoutFor(startup);
        bool english = _mainMenu.Language == StartupLanguage.English;
        string[] labels = english
            ? ["PROFILE", "VIEW DISTANCE", "ANTI-ALIASING*", "SHADOWS", "SHADOW RESOLUTION*", "CLOUDS", "VOLUMETRIC FOG", "BLOOM", "BRIGHTNESS"]
            : ["PROFIL", "DOHLED", "VYHLAZENI HRAN*", "STINY", "ROZLISENI STINU*", "MRAKY", "OBJEMOVA MLHA", "BLOOM", "JAS"];
        string profile = _playerOptions.AutomaticGraphics
            ? "AUTO"
            : _playerOptions.CustomGraphics
                ? "CUSTOM"
                : GraphicsProfileName(_playerOptions.GraphicsQuality, _mainMenu.Language);
        string[] values =
        [
            profile,
            $"{_playerOptions.ViewDistance} CHUNKS",
            $"{_playerOptions.MsaaSamples}X MSAA",
            _playerOptions.Shadows ? "ON" : "OFF",
            $"{_playerOptions.MapDetailSize} PX",
            EffectQualityName(_playerOptions.CloudQuality, english),
            EffectQualityName(_playerOptions.FogQuality, english),
            _playerOptions.Bloom ? "ON" : "OFF",
            $"{BrightnessPercent(_playerOptions.NightBrightness)} %",
        ];

        _text.DrawText(english ? "GRAPHICS / PERFORMANCE" : "GRAFIKA / VYKON",
            (int)startup.Panel.X + 52, (int)startup.Panel.Y + 36, 3);
        DrawCenteredText(english ? "BACK" : "ZPET", layout.Back, (int)layout.Back.Y + 11, 2);
        int summaryCharacters = Math.Max(28, (int)((startup.Panel.Width - 140f) / 8f));
        _text.DrawText(ShortUiLine(_graphicsDetectionSummary, summaryCharacters),
            (int)startup.Panel.X + 70, (int)startup.Panel.Y + 86, 1);

        for (int row = 0; row < labels.Length; row++)
        {
            UiBox minus = layout.Minus[row];
            UiBox plus = layout.Plus[row];
            DrawCenteredText("<", minus, (int)minus.Y + 11, 2);
            DrawCenteredText(">", plus, (int)plus.Y + 11, 2);
            _text.DrawText(labels[row], (int)minus.X + 68, (int)minus.Y + 5, 1);
            _text.DrawText(values[row], (int)minus.X + 68, (int)minus.Y + 22, 1);
        }

        string hint = english
            ? "TAB selects a row, LEFT/RIGHT changes it.  * applies after restart."
            : "TAB vybere radek, VLEVO/VPRAVO meni hodnotu.  * plati po restartu.";
        _text.DrawText(hint, (int)startup.Panel.X + 70,
            (int)(startup.Panel.Y + startup.Panel.Height - 42), 1);
        if (_graphicsRestartRequired)
        {
            _text.DrawText(english ? "RESTART REQUIRED FOR MARKED CHANGES" : "OZNACENE ZMENY VYZADUJI RESTART",
                (int)startup.Panel.X + 70, (int)(startup.Panel.Y + startup.Panel.Height - 66), 1);
        }
    }

    private static string EffectQualityName(int quality, bool english) => quality switch
    {
        <= 0 => "OFF",
        1 => english ? "LOW" : "NIZKA",
        2 => english ? "MEDIUM" : "STREDNI",
        _ => english ? "HIGH" : "VYSOKA",
    };

    private void DrawModManagerSprites(
        StartupLayout startup,
        Vector2 pointer,
        Vector4 edge,
        Vector4 field,
        Vector4 button,
        Vector4 buttonHover,
        Vector4 quiet)
    {
        if (_sprites is null || _modManager is null) return;
        ModManagerLayout layout = ModManagerLayoutFor(startup);
        _sprites.DrawRect(layout.Back.X, layout.Back.Y, layout.Back.Width, layout.Back.Height,
            layout.Back.Contains(pointer) ? buttonHover : field);
        _sprites.DrawRect(layout.Apply.X, layout.Apply.Y, layout.Apply.Width, layout.Apply.Height,
            !_modManager.HasPendingChanges
                ? quiet
                : layout.Apply.Contains(pointer) ? buttonHover : button);
        _sprites.DrawRect(layout.Previous.X, layout.Previous.Y, layout.Previous.Width, layout.Previous.Height,
            _modManagerPage == 0 ? quiet : field);
        _sprites.DrawRect(layout.Next.X, layout.Next.Y, layout.Next.Width, layout.Next.Height,
            _modManagerPage >= ModManagerPageCount - 1 ? quiet : field);

        int start = _modManagerPage * VisibleModRows;
        IReadOnlyList<InstalledModInfo> mods = _modManager.Mods;
        for (int row = 0; row < VisibleModRows; row++)
        {
            UiBox box = ModRow(layout, row);
            if (start + row >= mods.Count)
            {
                _sprites.DrawRect(box.X, box.Y, box.Width, box.Height, quiet);
                continue;
            }

            InstalledModInfo mod = mods[start + row];
            Vector4 rowColor = mod.Enabled
                ? new Vector4(0.10f, 0.28f, 0.17f, 1f)
                : new Vector4(0.16f, 0.105f, 0.105f, 1f);
            if (box.Contains(pointer)) rowColor *= 1.18f;
            rowColor.W = 1f;
            _sprites.DrawRect(box.X, box.Y, box.Width, box.Height, rowColor);
            _sprites.DrawFrame(box.X, box.Y, box.Width, box.Height, 1f, mod.Enabled ? edge : field);
        }
    }

    private void DrawModManagerText(StartupLayout startup)
    {
        if (_text is null || _modManager is null || _mainMenu is null) return;
        bool czech = _mainMenu.Language == StartupLanguage.Czech;
        ModManagerLayout layout = ModManagerLayoutFor(startup);
        DrawCenteredText(czech ? "SPRAVCE MODU" : "MOD MANAGER", startup.Panel, (int)startup.Panel.Y + 34, 4);
        DrawCenteredText(czech ? "ZPET" : "BACK", layout.Back, (int)layout.Back.Y + 11, 2);
        DrawCenteredText(
            _modManager.HasPendingChanges
                ? czech ? "POUZIT A RESTARTOVAT" : "APPLY & RESTART"
                : czech ? "BEZE ZMEN" : "NO CHANGES",
            layout.Apply,
            (int)layout.Apply.Y + 11,
            1);

        IReadOnlyList<InstalledModInfo> mods = _modManager.Mods;
        int start = _modManagerPage * VisibleModRows;
        for (int row = 0; row < VisibleModRows && start + row < mods.Count; row++)
        {
            InstalledModInfo mod = mods[start + row];
            UiBox box = ModRow(layout, row);
            string state = mod.Enabled ? "[ON]" : "[OFF]";
            string pending = mod.Enabled == mod.Loaded ? string.Empty : " *";
            _text.DrawText(
                ShortUiLine($"{state} {mod.Name}  v{mod.Version}{pending}", 62),
                (int)box.X + 12,
                (int)box.Y + 8,
                2);
            _text.DrawText(
                ShortUiLine($"{mod.Id}  |  {mod.Trust}", 92),
                (int)box.X + 12,
                (int)box.Y + 29,
                1);
        }

        DrawCenteredText("<", layout.Previous, (int)layout.Previous.Y + 8, 2);
        DrawCenteredText(
            $"{_modManagerPage + 1} / {ModManagerPageCount}",
            startup.Panel,
            (int)layout.Previous.Y + 9,
            1);
        DrawCenteredText(">", layout.Next, (int)layout.Next.Y + 8, 2);
        if (mods.Count == 0)
        {
            DrawCenteredText(
                czech ? "NEJSOU NAINSTALOVANE ZADNE MODY" : "NO MODS INSTALLED",
                startup.Panel,
                (int)startup.Panel.Y + 286,
                2);
        }
        else
        {
            DrawCenteredText(
                czech ? "KLIKNI NA MOD PRO ZAPNUTI NEBO VYPNUTI" : "CLICK A MOD TO ENABLE OR DISABLE IT",
                startup.Panel,
                (int)(startup.Panel.Y + startup.Panel.Height - 74),
                1);
        }
    }

    /// <summary>
    /// Pravý panel titulní obrazovky: stručně řekne stav prototypu, první herní krok i
    /// všechny aktuální klávesy. Řádky jsou v lokalizační tabulce už záměrně krátké, aby
    /// se vešly i vedle formuláře na menším okně.
    /// </summary>
    private void DrawStartupInfo(StartupLocalization.Copy copy, UiBox info)
    {
        if (_text is null)
        {
            return;
        }

        int maxCharacters = Math.Max(22, (int)((info.Width - 24f) / TextRenderer.AdvanceX));
        int x = (int)info.X + 12;
        int y = (int)info.Y + 12;

        DrawCenteredText(copy.EarlyAccessTitle, info, y, 2);
        y += 24;
        DrawStartupLines(copy.EarlyAccessLines, x, ref y, maxCharacters);

        y += 6;
        DrawCenteredText(copy.FirstStepsTitle, info, y, 2);
        y += 24;
        DrawStartupLines(copy.FirstStepsLines, x, ref y, maxCharacters);

        y += 6;
        DrawCenteredText(copy.ControlsTitle, info, y, 2);
        y += 24;
        DrawStartupLines(copy.ControlsLines, x, ref y, maxCharacters);
    }

    private void DrawStartupLines(IEnumerable<string> lines, int x, ref int y, int maxCharacters)
    {
        foreach (string line in lines)
        {
            _text!.DrawText(ShortUiLine(line, maxCharacters), x, y, 1);
            y += 12;
        }
    }

    private void DrawCenteredText(string value, UiBox box, int y, int scale)
    {
        int width = value.Length * TextRenderer.AdvanceX * scale;
        int x = (int)(box.X + ((box.Width - width) * 0.5f));
        _text?.DrawText(value, x, y, scale);
    }

    private static string ShortUiLine(string value, int maxLength)
    {
        string oneLine = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return oneLine.Length <= maxLength ? oneLine : oneLine[..(maxLength - 3)] + "...";
    }

    private void DrawOverlay()
    {
        if (_text is null)
        {
            return;
        }

        // Overlay patří nad scénu, takže bez hloubkového testu, a bez cullingu, protože
        // ortogonální projekce obrací osu Y a navíjení textových obdélníků se převrátí.
        // V OpenGL se to nastavovalo tady voláními glDisable; ve Vulkanu je obojí zapečené
        // v pipeline textu (BlendMode.Overlay, CullModeFlags.None) a měnit se za běhu nedá.
        // IKONY PRVNÍ, TEXT AŽ POTOM, A OD KAŽDÉHO JEDNA DÁVKA.
        //
        // Vykreslovač textu má jeden buffer na snímek. Druhé Begin/End v témž snímku proto
        // přepíše vrcholy té první dávky dřív, než je grafika nakreslí — a z první zbude
        // obsah druhé. Přesně takhle zmizel řádek s FPS, když jsem mezi text vložil ikony.
        if (_sprites is not null && _inventoryScreen is not null && _inventory is not null
            && _recipes is not null && _items is not null)
        {
            _sprites.Begin(ClientSize.X, ClientSize.Y);
            _inventoryScreen.Draw(_sprites, _inventory, _recipes, ClientSize.X, ClientSize.Y);

            DrawVitals();

            // Nesená hromádka pod kurzorem, ne ve slotu — jinak není poznat, že ji hráč drží.
            if (_inventoryScreen.Open && !_inventory.Held.IsEmpty)
            {
                float carried = InventoryScreen.SlotSize(ClientSize.Y) * 0.7f;

                _inventoryScreen.DrawItemIcon(
                    _sprites,
                    _inventory.Held.Item,
                    MouseState.Position.X - (carried * 0.5f),
                    MouseState.Position.Y - (carried * 0.5f),
                    carried);
            }

            // POPISEK CÍLE. Rám patří do dávky spritů, text až do té textové — jinak by ho
            // sprity překreslily, protože se kreslí dřív.
            if (_inventoryScreen?.Open != true && !_pauseMenuOpen)
            {
                _aimInfo = AimTarget();
                if (_aimInfo is { } frame) DrawAimPanelFrame(frame);
            }
            else
            {
                _aimInfo = null;
            }

            _sprites.End();
        }

        _text.Begin(ClientSize.X, ClientSize.Y);
        if (_aimInfo is { } label) DrawAimPanelText(label);
        _text.DrawText(
            DebugOverlay.FpsLine(_frameTimer.SmoothedFps, _frameTimer.TotalStats().MaxMs, UiLanguage), 8, 8);
        _text.DrawText(DebugOverlay.PositionLine(_player.Position), 8, 8 + (TextRenderer.LineHeight * 2));
        _text.DrawText(DebugOverlay.DrawCallLine(_stats.DrawCalls), 8, 8 + (TextRenderer.LineHeight * 4));
        _text.DrawText(MemoryLine(), 8, 8 + (TextRenderer.LineHeight * 6));
        _text.DrawText(CullingLine(), 8, 8 + (TextRenderer.LineHeight * 8));

        if (_inventoryScreen is not null && _inventory is not null && _recipes is not null)
        {
            _inventoryScreen.DrawText(_text, _inventory, _recipes, ClientSize.X, ClientSize.Y, UiLanguage);

            if (_inventoryScreen.Open && !_inventory.Held.IsEmpty && _inventory.Held.Count > 1)
            {
                _text.DrawText(
                    _inventory.Held.Count.ToString(CultureInfo.InvariantCulture),
                    (int)MouseState.Position.X,
                    (int)MouseState.Position.Y,
                    2);
            }
        }

        if (_hotbar is not null)
        {
            string line = _chiselMode
                ? DebugOverlay.ChiselLine(_chisel.Mode.ToString(), _chisel.Size, _hotbar.SelectedName, UiLanguage)
                : HandLine();

            _text.DrawText(line, 8, ClientSize.Y - (TextRenderer.LineHeight * 3));
        }

        if (_powerNoticeSeconds > 0f && !string.IsNullOrEmpty(_powerNotice))
        {
            _text.DrawText(_powerNotice, 8, ClientSize.Y - (TextRenderer.LineHeight * 6), 1);
        }

        if (!_creative && _inventoryScreen?.Open != true)
        {
            int centreTop = AimPanelBottom();

            string? goal = SurvivalGoalText();
            if (!string.IsNullOrEmpty(goal))
            {
                int goalX = Math.Max(8, (ClientSize.X - (goal.Length * TextRenderer.AdvanceX)) / 2);
                _text.DrawText(goal, goalX, centreTop, 1);
            }

            string? harvestHint = HarvestRequirementText();
            if (!string.IsNullOrEmpty(harvestHint))
            {
                int hintX = Math.Max(8, (ClientSize.X - (harvestHint.Length * TextRenderer.AdvanceX)) / 2);
                _text.DrawText(hintX == 0 ? harvestHint : harvestHint, hintX,
                    centreTop + (TextRenderer.LineHeight * 2), 1);
            }
        }

        DrawDebugMenu();
        DrawCommanderPanel();
        DrawLookMenu();

        // ZAMĚŘOVAČ JEN VE HŘE. S otevřeným panelem hráč nemíří na svět, ale klikáním do
        // slotů — křížek uprostřed inventáře je pak jen smetí přes obsah.
        // ZAMĚŘOVAČ JEN Z PRVNÍ OSOBY. Ve třetí neukazuje na to, kam hráč sáhne — a v pohledu
        // zepředu se kamera dívá proti směru míření, takže je vyloženě matoucí.
        if (_inventoryScreen?.Open != true && _cameraView == CameraView.First)
        {
            DrawCrosshair();
        }

        _text.End(_stats);

        // POPISEK AŽ ÚPLNĚ NAKONEC, ve vlastních dávkách.
        //
        // Rám i text musí ležet nad vším ostatním. Sprity se kreslí před textem, takže rám
        // v první dávce byl pod jmény receptů a ta jím prosvítala; a text popisku by se
        // v první textové dávce pral se zbytkem. Dvě dávky navíc to řeší úplně.
        if (_sprites is not null && _inventoryScreen?.Open == true
            && _inventory is not null && _recipes is not null)
        {
            _sprites.Begin(ClientSize.X, ClientSize.Y);
            _inventoryScreen.DrawTooltipFrame(_sprites, _inventory, _recipes, ClientSize.X, ClientSize.Y, UiLanguage);
            _sprites.End();

            _text.Begin(ClientSize.X, ClientSize.Y);
            _inventoryScreen.DrawTooltipText(_text, _inventory, _recipes, ClientSize.X, ClientSize.Y, UiLanguage);
            _text.End(_stats);
        }
    }

    /// <summary>
    /// Obrys tvaru, na který hráč právě míří.
    ///
    /// <para><b>Obkresluje se skutečný tvar, ne blok.</b> Kmen je sloupec dílků, listí kolem
    /// něj druhá vrstva téhož bloku, trs trávy dvě zkřížené plochy a otesaný blok hromádka
    /// kvádrů — a obrys musí sedět na to, co paprsek opravdu trefil. Jinak by hráč viděl
    /// rámeček kolem celé kostky a nevěděl, který kousek se chystá odstranit.</para>
    ///
    /// <para>Paprsek se střílí každý snímek. Je to týž výpočet, jaký rozhoduje o bourání,
    /// takže se obrys nemůže rozejít s tím, co kliknutí opravdu udělá.</para>
    /// </summary>
    private void DrawAimOutline(Matrix4 viewProjection)
    {
        if (_outline is null || _world is null || _player is null)
        {
            return;
        }

        // NA VODU SE ZAMĚŘUJE JEN S KBELÍKEM. Jinak paprsek hladinou projde, takže obrys
        // sedí na tom, do čeho se dá opravdu bouchnout - a pod vodou nezůstane viset kolem
        // bloku, ve kterém má hráč hlavu.
        if (!MicroRaycast.Cast(
            _world, _player.EyePosition, _camera.Forward, ReachDistance, out MicroHit hit,
            hitLiquid: HoldingBucket()))
        {
            return;
        }

        var origin = new Vector3(hit.Block.X, hit.Block.Y, hit.Block.Z);

        // MATICE MUSÍ PROJÍT VULKANSKOU KOREKCÍ, stejně jako ji používá ChunkRenderer.
        // OpenTK skládá projekci pro GL, kde je v clip prostoru osa Y nahoru a hloubka
        // od −1; Vulkan má Y dolů a hloubku od 0. Bez korekce vyšel obrys mnohonásobně
        // větší než blok a rozházený do stran — přesně tak to ve hře vypadalo.
        _outline.Begin((viewProjection));

        ushort aimed = _world.GetBlock(hit.Block.X, hit.Block.Y, hit.Block.Z);

        // Klacik, kaminek i pazourek maji skutecne male Blockbench kvadry a kazdy je
        // stabilne otoceny vlastnim uhlem. Raycast uz tento tvar pouziva; outline musi
        // pouzit tentyz, jinak by kolem nalezu zustala zavadejici cela kostka bloku.
        if (_world.GetMicro(hit.Block.X, hit.Block.Y, hit.Block.Z) is null
            && _world.Registry.ShapeOf(aimed) == BlockShape.GroundClutter
            && _world.Registry.GroundModelOf(aimed) is { } model)
        {
            float angle = GroundClutterShape.AngleRadians(hit.Block.X, hit.Block.Y, hit.Block.Z);
            Vector3 centre = origin + new Vector3(0.5f, 0f, 0.5f);

            foreach (GroundClutterModel.Box box in model.Boxes)
            {
                _outline.DrawOrientedBox(
                    new Aabb(origin + box.Min, origin + box.Max), centre, angle);
            }

            _outline.End(_stats);
            return;
        }

        if (_world.GetMicro(hit.Block.X, hit.Block.Y, hit.Block.Z) is null
            && _world.Registry.ShapeOf(aimed) == BlockShape.HytaleModel
            && _world.Registry.HytaleModelOf(aimed) is { } hytale)
        {
            Vector3 anchor = origin + new Vector3(0.5f, 0f, 0.5f);
            _outline.DrawBox(new Aabb(anchor + hytale.Bounds.Min, anchor + hytale.Bounds.Max));
            _outline.End(_stats);
            return;
        }

        if (_world.GetMicro(hit.Block.X, hit.Block.Y, hit.Block.Z) is null
            && _world.Registry.ShapeOf(aimed) == BlockShape.Torch)
        {
            DrawTorchOutline(origin, _world.GetPieces(hit.Block.X, hit.Block.Y, hit.Block.Z));
            _outline.End(_stats);
            return;
        }

        // Rostlina se obkresluje po plochách, ne kvádrem. Obě plochy jdou přes
        // úhlopříčky bloku, takže kvádr kolem nich je skoro celý blok a rámeček by
        // vypadal, jako by hráč mířil na kostku.
        if (_world.GetMicro(hit.Block.X, hit.Block.Y, hit.Block.Z) is null
            && _world.Registry.ShapeOf(aimed) == BlockShape.Cross)
        {
            bool submerged = _world.ContainsWater(hit.Block.X, hit.Block.Y, hit.Block.Z)
                || _world.ContainsWater(hit.Block.X, hit.Block.Y + 1, hit.Block.Z);

            (PlantShape.Plane first, PlantShape.Plane second) =
                PlantShape.Planes(hit.Block.X, hit.Block.Y, hit.Block.Z, submerged);

            // OBRYS SE STÁHNE NA KRESBU. Plocha jde přes celou úhlopříčku bloku, ale
            // nakreslená je z ní jen kytka uprostřed — rámeček kolem celé plochy proto
            // vypadal, jako by hráč mířil na kostku. Rozsah kresby se bere z alfy
            // dlaždice, takže obrys sedí i na rostliny, které v textuře teprve přibudou.
            Vector4 ink = _textures?.InkBounds(_registry!.FaceLayer(aimed, BlockFace.PosX))
                ?? new Vector4(0f, 0f, 1f, 1f);

            DrawPlantPlane(origin, first, ink);
            DrawPlantPlane(origin, second, ink);
        }
        else
        {
            foreach (Aabb box in AimBoxes(hit))
            {
                _outline.DrawBox(new Aabb(origin + box.Min, origin + box.Max));
            }
        }

        _outline.End(_stats);
    }

    /// <summary>Obkresli osmiboky nakloneny drik pochodne misto celeho voxelu.</summary>
    private void DrawTorchOutline(Vector3 origin, byte state)
    {
        (Vector3 localBottom, Vector3 localTop) = PieceMask.TorchAxis(state);
        Vector3 bottom = origin + localBottom;
        Vector3 top = origin + localTop;
        Vector3 axis = Vector3.Normalize(top - bottom);
        Vector3 reference = MathF.Abs(Vector3.Dot(axis, Vector3.UnitY)) > 0.94f
            ? Vector3.UnitX
            : Vector3.UnitY;
        Vector3 sideA = Vector3.Normalize(Vector3.Cross(axis, reference));
        Vector3 sideB = Vector3.Normalize(Vector3.Cross(axis, sideA));
        const int sides = 8;

        for (int i = 0; i < sides; i++)
        {
            float a0 = MathF.Tau * i / sides;
            float a1 = MathF.Tau * (i + 1) / sides;
            Vector3 r0 = (sideA * MathF.Cos(a0) + sideB * MathF.Sin(a0)) * PieceMask.TorchRadius;
            Vector3 r1 = (sideA * MathF.Cos(a1) + sideB * MathF.Sin(a1)) * PieceMask.TorchRadius;
            _outline!.DrawLine(bottom + r0, bottom + r1);
            _outline.DrawLine(top + r0, top + r1);
            _outline.DrawLine(bottom + r0, top + r0);
        }
    }

    /// <summary>
    /// Obrys jedné plochy trsu, stažený na tu její část, kde je v textuře kresba.
    /// </summary>
    /// <param name="ink">Rozsah kresby v UV: xy levý horní roh, zw pravý dolní.</param>
    private void DrawPlantPlane(Vector3 origin, PlantShape.Plane plane, Vector4 ink)
    {
        var from = new Vector3(plane.From.X, 0f, plane.From.Y);
        var to = new Vector3(plane.To.X, 0f, plane.To.Y);

        // Vodorovně: úsečka se ořízne na tu část, kterou kresba zabírá.
        Vector3 direction = to - from;
        Vector3 tight = from + (direction * ink.X);
        Vector3 tightTo = from + (direction * ink.Z);

        // Svisle: řádek 0 textury je vršek obrázku, ale leží nahoře ve světě, takže se
        // rozsah obrací.
        float height = plane.Top - plane.Bottom;
        float bottom = plane.Bottom + (height * (1f - ink.W));
        float top = plane.Bottom + (height * (1f - ink.Y));

        _outline!.DrawPlane(origin + tight, origin + tightTo, origin.Y + bottom, origin.Y + top);
    }

    /// <summary>Kvádry, ze kterých se skládá tvar pod zaměřovačem. V souřadnicích bloku.</summary>
    private IEnumerable<Aabb> AimBoxes(MicroHit hit)
    {
        MicroBlock? micro = _world!.GetMicro(hit.Block.X, hit.Block.Y, hit.Block.Z);

        if (micro is not null)
        {
            foreach (Aabb collider in _world.MicroShapes.Get(micro).Colliders)
            {
                yield return collider;
            }

            yield break;
        }

        ushort block = _world.GetBlock(hit.Block.X, hit.Block.Y, hit.Block.Z);

        // OBRYS VODY SEDÍ NA HLADINĚ, ne na stropě bloku. Mělčina zabírá jen zlomek bloku,
        // takže rámeček kolem celé kostky nad ní vypadal, jako by hráč mířil o patro výš.
        // Dveře jsou jeden předmět, i když ve světě zabírají dva voxely. Dvě samostatné
        // krabice by nakreslily vodorovnou hranu přes jejich střed, proto vracíme jediný
        // dvoublokový panel. Při zásahu horní poloviny začíná lokálně o blok níž.
        if (_world.Registry.ShapeOf(block) == BlockShape.Door)
        {
            string id = _world.Registry.Definition(block).Id;
            int localBottom = id == "tesseris:wooden_door_top" ? -1 : 0;
            int bottomY = hit.Block.Y + localBottom;
            bool completeDoor = _world.Registry.Definition(
                    _world.GetBlock(hit.Block.X, bottomY, hit.Block.Z)).Id
                == "tesseris:wooden_door"
                && _world.Registry.Definition(
                    _world.GetBlock(hit.Block.X, bottomY + 1, hit.Block.Z)).Id
                == "tesseris:wooden_door_top";

            if (completeDoor)
            {
                byte doorMask = _world.GetPieces(hit.Block.X, bottomY, hit.Block.Z);
                Aabb panel = PieceMask.DoorCollider(doorMask, height: 2f);
                var offset = new Vector3(0f, localBottom, 0f);
                yield return new Aabb(panel.Min + offset, panel.Max + offset);
                yield break;
            }
        }

        if (_world.Registry.IsLiquid(block))
        {
            byte level = _world.GetFluid(hit.Block.X, hit.Block.Y, hit.Block.Z);

            // Voda pod vodou vyplňuje blok celý - hladina má smysl jen tam, kde nad ní
            // začíná vzduch. Táž úvaha jako v mesheru, viz FluidCell.Continuous.
            bool covered = _world.ContainsWater(
                hit.Block.X, hit.Block.Y + 1, hit.Block.Z);

            float top = covered ? 1f : FluidCell.Height(level) - ChunkMesher.WaterSurfaceDrop;

            yield return new Aabb(Vector3.Zero, new Vector3(1f, MathF.Max(top, 0.05f), 1f));

            yield break;
        }

        // Rostlina je jen dvojice ploch. Obrys se kreslí kolem kvádru, do kterého se vejdou —
        // hrany samotných ploch by byly dvě zkřížené čáry a v trávě by zanikly.
        if (_world.Registry.ShapeOf(block) == BlockShape.Cross)
        {
            bool submerged = _world.ContainsWater(hit.Block.X, hit.Block.Y, hit.Block.Z)
                || _world.ContainsWater(hit.Block.X, hit.Block.Y + 1, hit.Block.Z);

            (PlantShape.Plane first, PlantShape.Plane second) =
                PlantShape.Planes(hit.Block.X, hit.Block.Y, hit.Block.Z, submerged);

            float minX = MathF.Min(MathF.Min(first.From.X, first.To.X), MathF.Min(second.From.X, second.To.X));
            float maxX = MathF.Max(MathF.Max(first.From.X, first.To.X), MathF.Max(second.From.X, second.To.X));
            float minZ = MathF.Min(MathF.Min(first.From.Y, first.To.Y), MathF.Min(second.From.Y, second.To.Y));
            float maxZ = MathF.Max(MathF.Max(first.From.Y, first.To.Y), MathF.Max(second.From.Y, second.To.Y));

            yield return new Aabb(
                new Vector3(minX, first.Bottom, minZ),
                new Vector3(maxX, first.Top, maxZ));

            yield break;
        }

        byte mask = _world.GetPieces(hit.Block.X, hit.Block.Y, hit.Block.Z);
        ExtraPieces extra = _world.GetExtra(hit.Block.X, hit.Block.Y, hit.Block.Z);

        // Obkresluje se jen ta vrstva, do které paprsek narazil. Rámeček kolem obou by
        // u kmene obalil i listí kolem něj a nebylo by poznat, co se odstraní.
        byte drawn = !extra.IsEmpty && extra.Block == hit.Material ? extra.Mask : mask;
        BlockShape shape = !extra.IsEmpty && extra.Block == hit.Material
            ? BlockShape.Cube
            : _world.Registry.ShapeOf(block);

        foreach (Aabb piece in PieceMask.Colliders(shape, drawn))
        {
            yield return piece;
        }
    }

    /// <summary>
    /// Zaměřovač uprostřed obrazovky.
    ///
    /// <para>Dvě čárky s vynechaným středem: plný křížek by zakryl přesně ten bod,
    /// na který se míří, což vadí u malých cílů — u stébla trávy nebo u dílku kmene.</para>
    ///
    /// <para>Kreslí se do téže dávky jako text, takže nestojí draw call navíc.</para>
    /// </summary>
    private void DrawCrosshair()
    {
        if (_text is null)
        {
            return;
        }

        const int Arm = 8;
        const int Thickness = 2;
        const int Gap = 3;

        int centreX = ClientSize.X / 2;
        int centreY = ClientSize.Y / 2;
        int half = Thickness / 2;

        _text.DrawRect(centreX - Gap - Arm, centreY - half, Arm, Thickness);
        _text.DrawRect(centreX + Gap, centreY - half, Arm, Thickness);
        _text.DrawRect(centreX - half, centreY - Gap - Arm, Thickness, Arm);
        _text.DrawRect(centreX - half, centreY + Gap, Thickness, Arm);
    }

    /// <summary>
    /// Postará se o dlaždice vzdáleného terénu: zahodí ty, které vypadly z dosahu,
    /// a nahraje hotové. Stavba samotná běží na worker vláknech.
    /// </summary>
    /// <summary>
    /// Odkud smí začít vzdálený terén, aby mezi ním a chunky nezůstala mezera.
    ///
    /// <para><b>Není to prostě dohled × 32.</b> Streamer se rozhoduje po chuncích:
    /// zameshuje ten, jehož <b>střed</b> je do dohledu. Kamera přitom stojí kdekoli
    /// uvnitř svého chunku, takže se vzdálenost od kamery a vzdálenost mezi chunky liší
    /// až o úhlopříčku chunku, tedy <c>32·√2 ≈ 45</c> bloků. Chunky proto spolehlivě
    /// pokrývají jen kruh o <c>dohled·32 − 45</c>.</para>
    ///
    /// <para>Dlaždice LOD se staví jen tam, kde <b>celá</b> leží za vnitřním poloměrem.
    /// Když se vnitřní poloměr nechal na <c>dohled·32</c>, vznikl mezi obojím pás až
    /// 45 bloků, který nekreslil nikdo — <b>a přesně tudy bylo vidět skrz</b>. Pás se
    /// posouval s tím, jak hráč přecházel hranice chunků, takže díry vznikaly a mizely
    /// při chůzi.</para>
    ///
    /// <para><b>A ani to nestačí, když se hráč hýbe.</b> Sada dlaždic se nepřepočítává
    /// každý frame, ale až po ujetí <c>64</c> bloků (velikost poloviny nejjemnější
    /// dlaždice), takže vnitřní hranice LOD je za hráčem pozadu **až o dalších 64 bloků**.
    /// K tomu při rychlém pohybu chunky na okraji dohledu ještě nestihly dogenerovat,
    /// takže skutečné pokrytí je menší než to jmenovité. Přesně proto se díry objevovaly
    /// hlavně za letu.</para>
    ///
    /// <para>Odečítá se proto <b>128 bloků</b> (čtyři chunky): 45 na zaokrouhlení podle
    /// středu chunku, 64 na zpoždění přepočtu a zbytek jako rezerva na streaming. Cena je
    /// prstenec dlaždic navíc <b>pod</b> chunky, kde ho depth test zahodí — vršek dlaždice
    /// leží o čtvrt bloku níž než plná geometrie. Jediné, co se tím kazí, jsou jeskyně
    /// v tom pásmu: LOD zná jen výškopis, takže vchod do jeskyně zaslepí. Ve 256 blocích
    /// je to pár pixelů, a je to výrazně lepší než díra.</para>
    /// </summary>
    /// <param name="completeRadiusBlocks">
    /// Kam až je svět skutečně hotový (<see cref="ChunkStreamer.CompleteRadiusBlocks"/>).
    /// Při rychlém letu je to výrazně míň než dohled a <b>tohle je ta část, kterou pevný
    /// odstup vyřešit nedokáže</b> — na jeden přechod hranice chunku připadá při dohledu 12
    /// kolem 2600 chunků k vygenerování a při 37 blocích za vteřinu se hranice překročí
    /// každou vteřinu. LOD proto zaskočí až tam, kam svět opravdu sahá.
    /// </param>
    private static float NearRadiusFor(int viewDistanceChunks, float completeRadiusBlocks)
    {
        float nominal = (viewDistanceChunks * Chunk.Size) - 128f;

        // Totéž odečtení i od skutečného dosahu: i ten je změřený po chuncích, takže platí
        // stejné zaokrouhlení podle středu chunku i stejné zpoždění přepočtu.
        float actual = completeRadiusBlocks - 128f;

        // Kvantizace na 64 bloků. Bez ní by se poloměr při letu měnil každou chvíli
        // a vzdálený terén by se přepočítával pořád dokola — což se už jednou stalo
        // a stavba to tehdy nestíhala dohnat.
        float value = MathF.Min(nominal, actual);

        // Spodní mez 192 bloků (šest chunků). Níž se LOD pustit nesmí: dlaždice je jen
        // výškopis po hrubších buňkách, takže i s minimem ze čtyř rohů leží kousek pod
        // terénem — a zblízka by to bylo poznat. Když svět nestíhá ani na 192 bloků,
        // je lepší chvíli díra než terasovité schodiště přes celou obrazovku.
        return MathF.Max(192f, MathF.Floor(value / 64f) * 64f);
    }

    private void UpdateFarTerrain()
    {
        if (_farTerrain is null || _chunkRenderer is null || _streamer is null)
        {
            return;
        }

        _farTerrain.NearRadius = NearRadiusFor(_streamer.ViewDistanceChunks, _streamer.CompleteRadiusBlocks);

        // Mlha se řídí tím, kam je vidět. Se zapnutým LOD je to jeho vnější poloměr;
        // bez něj hranice chunků. Kdyby zůstala na chunkové vzdálenosti, spolkla by
        // celý vzdálený terén a LOD by nebyl k ničemu.
        // Kde přesně končí plná geometrie. Vzdálený terén se blíž nekreslí vůbec, takže
        // se nemůže objevit mezi chunky ani prosvítat vykopanou jamou.
        //
        // Odečítá se úhlopříčka chunku: streamer se rozhoduje po chuncích, takže na okraji
        // dohledu jsou některé sloupce hotové a jiné ne. Kdyby řez ležel přesně na dohledu,
        // vznikl by v tom pásu proužek, kde nekreslí nikdo.
        float fullGeometry = MathF.Min(
            _streamer.CompleteRadiusBlocks, _streamer.ViewDistanceChunks * Chunk.Size);

        _chunkRenderer.FarNearCutoff = MathF.Max(0f, fullGeometry - 46f);

        // KORUNY LOD NESMĚJÍ DO PŘEKRYVU S PLNÝMI CHUNKY. Neprůhledná zem je schovaná
        // depth testem, ale děravé křížené plochy korun z ní mohou vyčnívat a vypadat jako
        // obří listové stěny. Rostliny se proto řežou až přesně na hranici plné geometrie.
        _chunkRenderer.FarPlantCutoff = MathF.Max(0f, fullGeometry);

        // HLADINA SE ŘEŽE AŽ NA SAMÉ HRANICI, bez těch 46 bloků.
        //
        // Odečtení má u terénu smysl: zaručuje, že mezi chunky a LOD nezůstane proužek,
        // kde nekreslí nikdo. Vzniklý překryv je u neprůhledné geometrie neškodný, protože
        // ho hloubkový test zahodí.
        //
        // Voda se ale MÍCHÁ a do hloubky nezapisuje, takže se v překryvu vykreslila dvakrát
        // — jednou z chunků, jednou z LOD — a dvojité míchání udělalo přes moře světlý pás
        // přesně v tom pásmu. U hladiny je proto lepší riskovat úzký proužek než mít
        // viditelnou obruč kolem hráče.
        _chunkRenderer.FarWaterCutoff = MathF.Max(0f, fullGeometry);

        // MLHA SE MĚŘÍ PODLE TOHO, CO JE OPRAVDU VIDĚT.
        //
        // Dřív se brala z dosahu vzdáleného terénu, tedy 6144 bloků, a začínala na 72 %
        // toho, tedy ve 4424 blocích. Jenže hráč vidí chunky do několika stovek bloků —
        // mlha tak v celém běžném rozsahu dělala PŘESNĚ NULU a scéna byla plochá až
        // k obzoru, bez náznaku vzdušné perspektivy.
        //
        // Teď se odvíjí od pásma plné geometrie: mlha nabíhá od jeho poloviny a končí kus
        // za jeho koncem, takže vzdálené kopce znatelně modrají, ale nezmizí.
        float visible = _farTerrain.Enabled ? _farTerrain.OuterRadius : _farTerrain.NearRadius;

        // Keep most of the rendered landscape clear. Fog is now a distant horizon blend,
        // not a wall just beyond the fully detailed chunks.
        float automaticFogStart = MathF.Max(512f, visible * 0.68f);
        float automaticFogEnd = MathF.Max(automaticFogStart + 256f, visible * 0.97f);

        _fogTuning.UpdateAutomatic(automaticFogStart, automaticFogEnd, visible);
        _chunkRenderer.FogStart = _playerOptions.FullView
            ? FogTuning.AbsoluteMaximumDistance - FogTuning.MinimumSpan
            : _fogTuning.Start;
        _chunkRenderer.FogEnd = _playerOptions.FullView
            ? FogTuning.AbsoluteMaximumDistance
            : _fogTuning.End;
        _chunkRenderer.VolumetricFog = !_playerOptions.FullView && _playerOptions.FogQuality > 0;

        // Hladina do shaderu. Zbytek si spočítá sám: rozdělí pohledový paprsek na část
        // pod vodou a nad vodou a na každou pustí jinou mlhu. Díky tomu se dá z vody
        // koukat ven a shora do hloubky, aniž by se tu cokoli přepínalo.
        //
        // JEDNA VÝJIMKA: suchá jeskyně pod úrovní moře. Shader pozná vodu jen porovnáním
        // výšky, takže by jeskyni sto bloků pod hladinou považoval za zatopenou — se
        // spektrálním útlumem by z ní byla černá díra, protože červená by se na té dráze
        // pohltila úplně. Když je tedy kamera pod úrovní moře, ale v běžném vzduchu,
        // pošle se hladina hluboko pod svět a shader žádnou vodu nevidí.
        //
        // Není to úplné řešení: fragment sám o sobě pořád nepozná, jestli je pod vodou.
        // Na to by mesher musel příznak zapéct do vrcholu. Tohle ale pokrývá případ,
        // který hráč opravdu potká — že v jeskyni stojí.
        bool eyesInLiquid = _world is not null
            && _world.ContainsWater(
                (int)MathF.Floor(_camera.Position.X),
                (int)MathF.Floor(_camera.Position.Y),
                (int)MathF.Floor(_camera.Position.Z));

        bool dryBelowSeaLevel = !eyesInLiquid && _camera.Position.Y < TerrainGenerator.SeaLevel;

        // HLADINA LEŽÍ O BLOK VÝŠ, NEŽ ŘÍKÁ SeaLevel.
        //
        // SeaLevel = 305 znamená, že blok 305 je poslední vodní — a jeho strop je tedy
        // na 306. Dokud se do shaderu posílalo rovnou 305, počítal se hráč stojící po pás
        // ve vodě za suchého: oči má v 305,62, práh ponoření byl 305,3.
        // Odečítá se ještě snížení hladiny, aby geometrie i shader mluvily o téže rovině.
        const float WaterTop = 1f - ChunkMesher.WaterSurfaceDrop;

        _chunkRenderer.SeaLevel = dryBelowSeaLevel
            ? -10000f
            : TerrainGenerator.SeaLevel + WaterTop;

        // Mlha zůstává vzdušná VŽDYCKY, i pod vodou — modrou tmu obstará shader jen na
        // té části paprsku, která opravdu vede vodou. Přepínat ji tady bylo špatně:
        // pod hladinou pak platila krátká modrá mlha i na břeh a oblohu nad vodou.
        _chunkRenderer.FogColor = ChunkRenderer.SkyColor;

        // Čas pro vlnění hladiny a pomalý přesun mraků. Zabaluje se po několika hodinách:
        // ve floatu je po delší době krok
        // mezi snímky větší než rozdíl, který má vlna urazit, a vlnění se začne trhat.
        // Perioda je násobek 2π dělený nejnižší úhlovou rychlostí, takže na švu nic
        // neskočí — nejpomalejší vlna má rychlost 1,05, viz water.frag. Násobek 2000
        // zároveň dává mrakům dvakrát pomalejší bezešvý průchod jejich periodou.
        const float TimeWrap = 2f * MathF.PI * 2000f / 1.05f;

        _chunkRenderer.Time = (float)(_clock.Elapsed.TotalSeconds % TimeWrap);

        // Mazací barva zůstává jen jako pojistka. Obloha se od téhle chvíle kreslí
        // fullscreen průchodem jako první, takže se k mazací barvě nic nedostane —
        // ale kdyby průchod kdy vypadl, ať je pozadí nebe a ne černá.
        if (_renderer is not null)
        {
            _renderer.ClearColor = ChunkRenderer.SkyColor;
        }

        // Vzdálená rovina musí sahat dál než mlha, jinak se terén ořízne dřív, než stihne
        // splynout s oblohou — a protože ořezává po ose pohledu, projeví se to tak, že
        // co je rovně před kamerou zmizí, ale po otočení se objeví.
        _camera.FarPlane = visible * 1.5f;

        foreach (FarTerrain.TileKey stale in _farTerrain.Update(_camera.Position))
        {
            _chunkRenderer.RemoveFar(stale);
        }

        // Nahrávání s časovým rozpočtem místo pevného počtu. Dlaždice se hodně liší
        // velikostí — nejvzdálenější má přes sto tisíc trojúhelníků, nejbližší zlomek —
        // takže pevný počet buď zdržuje, nebo nestíhá.
        long start = Stopwatch.GetTimestamp();
        double budgetTicks = Stopwatch.Frequency * 0.002;

        while (_farTerrain.TryTakeFinished(out FarTerrain.FinishedTile tile))
        {
            // Mezi zadáním stavby a jejím dokončením se hráč mohl přesunout. Dlaždice,
            // která už není potřeba, se zahodí — nahrát ji znamená nechat ji v rendereru
            // navždy, protože ze seznamu k zahození už vypadla.
            if (!_farTerrain.IsWanted(tile.Key))
            {
                // Uvolnit klíč je nutné: jinak zůstane mezi rozpracovanými a dlaždice
                // se už nikdy nezadá ke stavbě, takže na jejím místě zůstane díra.
                _farTerrain.Discard(tile.Key);
                _farTerrain.Recycle(tile);
                continue;
            }

            _chunkRenderer.UploadFar(tile.Key, tile.Mesh, tile.Water, tile.Plants, tile.Min, tile.Max);
            _farTerrain.MarkUploaded(tile.Key);

            // Nahrání je kopie do bufferu, takže pracovní paměť je hned volná a smí se
            // vrátit do zásoby. Bez toho je z ní odpad pro úklid paměti.
            _farTerrain.Recycle(tile);

            if (Stopwatch.GetTimestamp() - start >= budgetTicks)
            {
                break;
            }
        }
    }

    /// <summary>Paměť: spravovaná halda a paměť zařízení drzená buffery.</summary>
    private string MemoryLine()
    {
        long managed = GC.GetTotalMemory(forceFullCollection: false);

        (int live, int peak) = VulkanBuffer.AllocationCount;
        string vram = string.Empty;

        return string.Create(
            CultureInfo.InvariantCulture,
            $"RAM: {managed / (1024.0 * 1024.0):F0} MB   bufferu: {live} (nejvic {peak})");
    }

    /// <summary>Kolik chunků a dlaždic culling zahodil a kolik jich zbylo.</summary>
    private string CullingLine()
    {
        if (_chunkRenderer is null)
        {
            return string.Empty;
        }

        int loaded = _chunkRenderer.LoadedChunks;
        int visible = _chunkRenderer.VisibleChunks;

        // „Ztracene" je tu jen když je otevřené menu — mimo něj se to nepočítá a stará
        // hodnota by lhala. Radši nic než číslo, o kterém se neví, jak je staré.
        string lost = _menu.Visible
            ? string.Create(CultureInfo.InvariantCulture, $"   Ztracene: {_streamer?.LostChunks ?? 0}")
            : string.Empty;

        // ODLOZENE DLAZDICE V OVERLAYI.
        //
        // Odlozena dlazdice se porad kresli, a to PRES tu novou (viz FarTerrain._retired).
        // Kdyz je z jineho pasma, ma teren zjednoduseny jinak, obe plochy se protinaji
        // a v kazdem pixelu vyhraje jednou jedna, jednou druha — z-fighting, ktery pri
        // chuzi zrni. Cislo, ktere pri chuzi neklesa zpatky k nule, je proto primo mericim
        // pristrojem na tuhle vadu.
        int retired = _farTerrain?.RetiredTiles ?? 0;

        return string.Create(
            CultureInfo.InvariantCulture,
            $"Culled: {loaded - visible}/{loaded} chunku, LOD {_chunkRenderer.FarTiles - _chunkRenderer.VisibleFarTiles}/{_chunkRenderer.FarTiles}, odlozeno {retired}, zvirata {_animals?.Count ?? 0}{lost}");
    }

    private void DrawDebugMenu()
    {
        if (_text is null || !_menu.Visible)
        {
            return;
        }

        // Vlevo pod overlayem. Vpravo se to neveslo: pri meritku 2 zabere radek pres
        // 600 pixelu a vylezl by z okna.
        const int Scale = 2;
        int x = 8;
        int y = 8 + (TextRenderer.LineHeight * Scale * 4);

        _text.DrawText("-- DEV MENU (F4 nebo F1) -- sipky, Shift x10", x, y, Scale);
        y += TextRenderer.LineHeight * Scale * 2;

        foreach (string line in _menu.Lines())
        {
            _text.DrawText(line, x, y, Scale);
            y += TextRenderer.LineHeight * Scale;
        }
    }

    /// <summary>
    /// Panel velitele: kolik je volných lidí, kolik je práce, co dělají stroje.
    /// </summary>
    /// <remarks>
    /// <para><b>Ukazuje se jen v režimu velitele</b> a to je záměr, ne šetření místem.
    /// první osoba přehled kolonie vidět NEMÁ. Kdyby panel svítil pořád,
    /// zmizí napětí mezi režimy, které je podle zadání hook hry.</para>
    ///
    /// <para>Volných lidí je první a největší číslo. Podle sekce 2 je to metrika, na které
    /// stojí celá hra.</para>
    /// </remarks>
    private void DrawCommanderPanel()
    {
        if (_imgui is null)
        {
            return;
        }

        _imgui.BeginFrame(
            ClientSize.X,
            ClientSize.Y,
            (float)_frameTimer.DeltaSeconds,
            new MouseSnapshot(
                MouseState.X,
                MouseState.Y,
                MouseState.IsButtonDown(MouseButton.Left),
                MouseState.IsButtonDown(MouseButton.Right),
                MouseState.IsButtonDown(MouseButton.Middle),
                MouseState.ScrollDelta.Y));

        // Sonda kreslí panel i mimo režim velitele, aby šla vykreslovací cesta ImGui
        // ověřit v selftestu — ten do režimu velitele nevstupuje.
        if (_commander.Commanding || _commander.InTransition || _uiProbe)
        {
            BuildCommanderWindows();
        }

        _imgui.Render(_renderer!.CommandBuffer);

        for (int i = 0; i < _imgui.LastDrawCalls; i++)
        {
            _stats.CountDrawCall();
        }
    }

    /// <summary>
    /// Ovládání velitele: myš označuje oblast v řezu, kolečko jím projíždí.
    /// </summary>
    /// <remarks>
    /// <para><b>Míří se na první pevný blok, do kterého paprsek narazí</b> (viz
    /// <see cref="CommanderPicking"/>). Rovina řezu zůstává jako STROP: bloky nad zvoleným
    /// patrem jsou pro paprsek průhledné, takže se do podzemí dostane sjetím řezu dolů.</para>
    ///
    /// <para><b>Q a E otáčí pohled po pravých úhlech.</b> Volné otáčení tady vědomě není —
    /// rozbilo by vztah „kam vlastně kliknu".</para>
    ///
    /// <para><b>Klikání do panelu se do světa nepropíše.</b> Bez toho by tažení posuvníku
    /// zároveň označovalo kus hory pod ním.</para>
    /// </remarks>
    private void HandleCommanderInput()
    {
        if (!_commander.Commanding || _commander.InTransition || _world is null)
        {
            return;
        }

        KeyboardState keyboardForBuild = KeyboardState;

        // Řez se posouvá kolečkem. Nahoru znamená výš, což odpovídá pohledu shora.
        int scroll = (int)MathF.Round(MouseState.ScrollDelta.Y);
        if (scroll != 0)
        {
            _commander.MoveSlice(scroll, 0, TerrainGenerator.WorldHeight - 1);
        }

        // OTÁČENÍ PO ČTVRTKRUZÍCH. Hrany se hlídají, aby jedno stisknutí byl jeden krok —
        // jinak by se za držení klávesy pohled protočil několikrát za frame.
        bool rotateLeft = keyboardForBuild.IsKeyDown(Keys.Q);
        if (rotateLeft && !_commanderRotateLeftWasDown)
        {
            _commander.RotateLeft();
        }

        bool rotateRight = keyboardForBuild.IsKeyDown(Keys.E);
        if (rotateRight && !_commanderRotateRightWasDown)
        {
            _commander.RotateRight();
        }

        _commanderRotateLeftWasDown = rotateLeft;
        _commanderRotateRightWasDown = rotateRight;

        if (ImGuiVulkanRenderer.WantsMouse)
        {
            return;
        }

        // Nářadí číslicemi. Jednička je označování, pak pás, drtič a vkládač.
        if (keyboardForBuild.IsKeyDown(Keys.D1)) { _tool = CommanderTool.Select; }
        if (keyboardForBuild.IsKeyDown(Keys.D2)) { _tool = CommanderTool.Belt; }
        if (keyboardForBuild.IsKeyDown(Keys.D3)) { _tool = CommanderTool.Crusher; }
        if (keyboardForBuild.IsKeyDown(Keys.D4)) { _tool = CommanderTool.Inserter; }
        if (keyboardForBuild.IsKeyDown(Keys.D5)) { _tool = CommanderTool.Demolish; }

        bool rotate = keyboardForBuild.IsKeyDown(Keys.R);
        if (rotate && !_buildRotateWasDown)
        {
            _buildFacing = (_buildFacing + 1) % BuildDirections.Length;
        }

        _buildRotateWasDown = rotate;

        if (!TryPickSliceCell(out Vector3i cell))
        {
            return;
        }

        bool left = MouseState.IsButtonDown(MouseButton.Left);

        // STAVĚNÍ MÁ PŘEDNOST PŘED OZNAČOVÁNÍM. Klikne se jednou, postaví se jedna věc —
        // tažení sem nepatří, jinak by jedno škubnutí myší postavilo dvacet pásů.
        if (_tool != CommanderTool.Select)
        {
            if (left && !_leftWasDown)
            {
                PlaceWithTool(cell);
            }

            if (MouseState.IsButtonDown(MouseButton.Right))
            {
                _tool = CommanderTool.Select;
            }

            return;
        }

        if (left && !_leftWasDown)
        {
            _selection.Begin(cell);
        }
        else if (left && _selection.Active)
        {
            _selection.DragTo(cell);
        }
        else if (!left && _selection.Active)
        {
            if (_selection.TryComplete(out Vector3i minimum, out Vector3i maximum))
            {
                ColonyRuntime.MarkResult result =
                    _colony.MarkAreaReachable(_world, _world.Registry, minimum, maximum);

                // ZAMÍTNUTÉ SE MUSÍ HLÁSIT. Tiché spolknutí nesplnitelné práce je přesně to,
                // kvůli čemu hráč nechápal, proč se nic nekope.
                _lastMark = result;
                Log.Info(result.Unreachable > 0
                    ? $"Oznaceno {result.Added} ukolu, {result.Unreachable} nedosazitelnych zamitnuto."
                    : $"Oznaceno {result.Added} ukolu.");
            }
            else
            {
                // Nad stropem: výběr zůstane rozdělaný, aby ho šlo zmenšit.
                Log.Warn($"Vyber je moc velky: {_selection.Volume} voxelu.");
            }
        }

        if (MouseState.IsButtonDown(MouseButton.Right))
        {
            _selection.Cancel();
        }
    }

    /// <summary>
    /// Postaví vybranou věc na zadanou buňku.
    /// </summary>
    /// <remarks>
    /// <para><b>Pás se napojí sám.</b> Když vedle něj stojí stroj, stane se z něj jeho vstup
    /// nebo výstup podle toho, na které straně leží — a jakmile má stroj obojí a proud,
    /// zautomatizuje se a pustí člověka. To je celý hook hry a nemá smysl po hráči chtít,
    /// aby to potvrzoval v nějakém dialogu.</para>
    /// </remarks>
    private void PlaceWithTool(Vector3i cell)
    {
        if (_world is null)
        {
            return;
        }

        // Stavět na obsazenou buňku nejde. Bourání má vlastní větev — to obsazenou buňku chce.
        if (_tool != CommanderTool.Demolish && _tool != CommanderTool.Inserter && _colony.IsOccupied(cell))
        {
            Log.Warn($"Na {cell} uz neco stoji. Zbourej to (5).");
            return;
        }

        switch (_tool)
        {
            case CommanderTool.Belt:
            {
                BeltSegment belt = _colony.PlaceBelt(cell, cells: 6, BuildDirections[_buildFacing]);
                ConnectBeltToNeighbours(cell, belt);
                Log.Info($"Pas na {cell}, smer {BuildDirections[_buildFacing]}.");
                break;
            }

            case CommanderTool.Crusher:
            {
                ushort ore = _world.Registry.IndexOf("tesseris:iron_ore");
                ushort dust = _world.Registry.IndexOf("tesseris:cobblestone");
                int machine = _colony.PlaceCrusher(cell, ore, dust);

                // Nový drtič je ruční, takže si k němu stoupne první volný člověk.
                for (int colonist = 0; colonist < _colony.Colonists.Count; colonist++)
                {
                    if (_colony.TryAssignOperator(machine, colonist))
                    {
                        break;
                    }
                }

                Log.Info($"Drtic na {cell}.");
                break;
            }

            case CommanderTool.Inserter:
            {
                PlaceInserterBetweenNeighbours(cell);
                break;
            }

            case CommanderTool.Demolish:
            {
                // Obsah zbouraného jde na sklad, takže je vidět, že se nic neztratilo.
                int stored = _colony.Colonists.StoredItems;
                ColonyRuntime.Demolished what = _colony.Demolish(cell);
                int recovered = _colony.Colonists.StoredItems - stored;

                Log.Info(what == ColonyRuntime.Demolished.Nothing
                    ? $"Na {cell} nic nestoji."
                    : $"Zbourano: {what} na {cell}, na sklad slo {recovered}.");
                break;
            }
        }
    }

    /// <summary>Napojí čerstvě postavený pás na stroj, který stojí vedle něj.</summary>
    private void ConnectBeltToNeighbours(Vector3i cell, BeltSegment belt)
    {
        for (int machine = 0; machine < _colony.Machines.Count; machine++)
        {
            if (_colony.Machines.IsRemoved(machine))
            {
                continue;
            }

            Vector3i at = _colony.Machines.CellOf(machine);
            Vector3i delta = cell - at;

            if (Math.Abs(delta.X) + Math.Abs(delta.Y) + Math.Abs(delta.Z) != 1)
            {
                continue;
            }

            // Pás před strojem je vstup, za ním výstup. Rozhoduje směr jízdy.
            Vector3i facing = BuildDirections[_buildFacing];
            bool feeds = (delta.X * facing.X) + (delta.Z * facing.Z) < 0;

            if (feeds)
            {
                _colony.Machines.SetInputBelt(machine, belt);
            }
            else
            {
                _colony.Machines.SetOutputBelt(machine, belt);
            }

            _colony.Machines.SetPowered(machine, true);
            return;
        }
    }

    /// <summary>Postaví vkládač mezi pás a stroj, které stojí vedle zadané buňky.</summary>
    private void PlaceInserterBetweenNeighbours(Vector3i cell)
    {
        BeltSegment? belt = null;
        int machine = -1;

        foreach (Vector3i offset in BuildDirections)
        {
            belt ??= _colony.BeltAt(cell + offset);

            for (int candidate = 0; candidate < _colony.Machines.Count; candidate++)
            {
                if (!_colony.Machines.IsRemoved(candidate) && _colony.Machines.CellOf(candidate) == cell + offset)
                {
                    machine = candidate;
                }
            }
        }

        if (belt is null || machine < 0)
        {
            Log.Warn("Vkladac potrebuje vedle sebe pas a stroj.");
            return;
        }

        _colony.Inserters.AddBeltToMachine(cell, belt, machine);
        _colony.CountInserter();
        Log.Info($"Vkladac na {cell}.");
    }

    /// <summary>Hrany kláves otáčení, aby jedno stisknutí byl jeden krok.</summary>
    private bool _commanderRotateLeftWasDown;

    private bool _commanderRotateRightWasDown;

    /// <summary>Jak dopadlo poslední označení. Pro panel — hráč musí vidět, co se zamítlo.</summary>
    private ColonyRuntime.MarkResult _lastMark;

    /// <summary>
    /// Na kterou buňku myš ukazuje: PRVNÍ PEVNÝ BLOK, do kterého paprsek narazí.
    /// </summary>
    /// <remarks>
    /// Jméno zůstalo, chování se změnilo: rovina řezu je teď jen strop a záloha. Rozhodování
    /// je v <see cref="CommanderPicking"/>, aby se dalo testovat bez okna a bez rendereru.
    /// </remarks>
    private bool TryPickSliceCell(out Vector3i cell)
    {
        cell = default;
        if (_world is null)
        {
            return false;
        }

        // Z pixelu na paprsek: střed obrazovky je střed záběru, okraje jsou půl zorného pole.
        float ndcX = ((MouseState.X / Math.Max(ClientSize.X, 1)) * 2f) - 1f;
        float ndcY = 1f - ((MouseState.Y / Math.Max(ClientSize.Y, 1)) * 2f);

        float tanHalf = MathF.Tan(MathHelper.DegreesToRadians(_camera.FieldOfViewDegrees) * 0.5f);
        float aspect = ClientSize.X / (float)Math.Max(ClientSize.Y, 1);

        Vector3 direction = Vector3.Normalize(
            _camera.ViewForward
            + (_camera.ViewRight * ndcX * tanHalf * aspect)
            + (_camera.ViewUp * ndcY * tanHalf));

        return CommanderPicking.TryPick(
            _world,
            _world.Registry,
            _camera.Position,
            direction,
            _commander.SliceY,
            out cell,
            out _);
    }

    /// <summary>
    /// Přepne mezi první osobou a velitelem i se vším, co k tomu patří.
    /// </summary>
    /// <remarks>
    /// <b>Jedno místo, ne dvě.</b> Tohle volá klávesa Tab i selftestová sonda. Dokud sonda
    /// přepínala <c>_commander.Toggle()</c> napřímo, neprošla nic z toho, co je kolem —
    /// a hlásila stav kurzoru, který sama nikdy nenastavila.
    /// </remarks>
    private void ToggleCommanderMode()
    {
        _commander.Toggle();

        if (!_commander.Commanding)
        {
            _selection.Cancel();

            // Zpátky do hry: kurzor se zase chytí. Bez zahození prvního posunu by kamera
            // skočila o celou vzdálenost, kterou myš ušla po panelu.
            CursorState = CursorState.Grabbed;
            _ignoreNextMouseDelta = true;
        }
        else
        {
            _commander.SetSlice((int)MathF.Floor(_player.Position.Y), 0, TerrainGenerator.WorldHeight - 1);

            // VELITEL POTŘEBUJE VIDĚT MYŠ. Označuje se tažením a mačkají se tlačítka
            // v panelu — bez viditelného kurzoru se nedá trefit nic.
            CursorState = CursorState.Normal;
        }

        Log.Info(_commander.Commanding ? "Rezim velitele." : "Prvni osoba.");
    }

    private void BuildCommanderWindows()
    {
        // DOLNÍ LEVÝ ROH, ne horní. Nahoře vlevo kreslí ladicí overlay (F3) a text se s panelem
        // překrýval tak, že nešlo přečíst ani jedno. Kotví se za spodní hranu, aby to platilo
        // v každém rozlišení — na výšku okna se spolehnout nedá.
        ImGui.SetNextWindowPos(
            new System.Numerics.Vector2(16f, ClientSize.Y - 16f),
            ImGuiCond.FirstUseEver,
            new System.Numerics.Vector2(0f, 1f));
        ImGui.SetNextWindowSize(new System.Numerics.Vector2(320f, 300f), ImGuiCond.FirstUseEver);

        if (ImGui.Begin("Kolonie"))
        {
            // TOHLE ČÍSLO JE CELÁ HRA. Sekce 2: lidé jsou nejvzácnější surovina.
            ImGui.TextColored(
                new System.Numerics.Vector4(0.55f, 0.9f, 0.55f, 1f),
                $"VOLNYCH LIDI: {_colony.FreeColonists} z {_colony.Colonists.Count}");

            if (_colony.FreedByAutomation > 0)
            {
                ImGui.TextColored(
                    new System.Numerics.Vector4(0.95f, 0.85f, 0.4f, 1f),
                    $"Automatizace uvolnila: {_colony.FreedByAutomation}");
            }

            ImGui.Separator();

            // NÁŘADÍ NAHOŘE. Bez toho hráč neví, co postaví, až klikne.
            ImGui.Text("Naradi (1-5, R otaci, prave tlacitko zpet)");
            ToolButton("1 Oznacit", CommanderTool.Select);
            ImGui.SameLine();
            ToolButton("2 Pas", CommanderTool.Belt);
            ImGui.SameLine();
            ToolButton("3 Drtic", CommanderTool.Crusher);
            ImGui.SameLine();
            ToolButton("4 Vkladac", CommanderTool.Inserter);
            ImGui.SameLine();
            ToolButton("5 Zbourat", CommanderTool.Demolish);

            if (_tool == CommanderTool.Belt)
            {
                Vector3i facing = BuildDirections[_buildFacing];
                ImGui.Text($"Smer pasu: {facing.X}, {facing.Z}");
            }

            ImGui.Separator();

            ImGui.Text($"Rez v patre: {_commander.SliceY} (Q/E otoci pohled)");
            ImGui.Text($"Navigace: {_colony.NavigationChunks} chunku ({_colony.PendingNavigation} ceka)");

            // NEDOSAŽITELNÉ SE MUSÍ UKÁZAT. Bez tohohle hráč jen vidí, že se nic nekope.
            if (_lastMark.Unreachable > 0)
            {
                ImGui.TextColored(
                    new System.Numerics.Vector4(1f, 0.6f, 0.35f, 1f),
                    $"Zamitnuto {_lastMark.Unreachable}: nevede k nim cesta");
            }

            ImGui.Separator();
            ImGui.Text("Prace");

            if (ImGui.BeginTable("prace", 2, ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders))
            {
                Row("volna", _colony.Jobs.OpenCount);
                Row("dela se", _colony.Jobs.ClaimedCount);
                Row("hotova", _colony.Jobs.DoneCount);
                Row("odlozena", _colony.Jobs.DeferredCount);
                ImGui.EndTable();
            }

            ImGui.Separator();

            // SKLAD PO DRUZICH. Jedno souhrnné číslo hráči neřekne, jestli má co jíst —
            // a přesně na tom teď stojí, kolik lidí kolonie unese.
            ImGui.Text($"Sklad: {_colony.Store.Total} kusu ve {_colony.Store.KindCount} druzich"
                + (_colony.Store.HasCell ? string.Empty : " (NENI RADNICE)"));

            if (_colony.Store.KindCount > 0
                && ImGui.BeginTable("sklad", 2, ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders))
            {
                for (int kind = 0; kind < _colony.Store.KindCount; kind++)
                {
                    ushort item = _colony.Store.ItemAt(kind);
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);

                    // Jídlo se odliší barvou: je to jediný druh, na kterém závisí, jestli
                    // se dá pracovat.
                    if (_colony.Store.IsFood(item))
                    {
                        ImGui.TextColored(
                            new System.Numerics.Vector4(0.55f, 0.9f, 0.55f, 1f),
                            ShortItemName(item));
                    }
                    else
                    {
                        ImGui.Text(ShortItemName(item));
                    }

                    ImGui.TableSetColumnIndex(1);
                    ImGui.Text(_colony.Store.CountAt(kind).ToString(CultureInfo.InvariantCulture));
                }

                ImGui.EndTable();
            }

            // HLAD MUSÍ BÝT VIDĚT NA ČÍSLECH, ne jen v kódu. Bez jídla roste a práce vázne.
            int starving = _colony.Colonists.StarvingCount;
            int worstHunger = _colony.Colonists.WorstHunger;
            int hungerPercent = worstHunger * 100 / ColonySimulation.MaxHunger;

            ImGui.TextColored(
                starving > 0
                    ? new System.Numerics.Vector4(1f, 0.4f, 0.4f, 1f)
                    : new System.Numerics.Vector4(1f, 1f, 1f, 1f),
                $"Hlad: nejvyssi {hungerPercent} %, jidla ve skladu {_colony.Store.FoodCount}, "
                + $"snedeno {_colony.Colonists.MealsEaten}");

            if (starving > 0)
            {
                ImGui.TextColored(
                    new System.Numerics.Vector4(1f, 0.4f, 0.4f, 1f),
                    $"HLADOVI {starving} z {_colony.Colonists.Count}: prace jde "
                    + $"{ColonySimulation.StarvingSlowdown}x pomaleji");
            }

            ImGui.Separator();
            ImGui.Text($"Stroju: {_colony.MachineCount} (pracuje {_colony.Machines.ActiveCount})");
            ImGui.Text($"Zdrceno: {_colony.Machines.CraftedTotal}");
            ImGui.Text($"Pasu: {_colony.BeltCount}, itemu na nich: {_colony.ItemsOnBelts}");
            ImGui.Text($"Vkladacu: {_colony.InserterCount}, prendaly: {_colony.Inserters.MovedTotal}");

            if (_selection.Active)
            {
                ImGui.Separator();
                ImGui.TextColored(
                    _selection.WithinLimit
                        ? new System.Numerics.Vector4(1f, 1f, 1f, 1f)
                        : new System.Numerics.Vector4(1f, 0.4f, 0.4f, 1f),
                    $"Vyber: {_selection.Volume} voxelu");
            }
        }

        ImGui.End();

        void ToolButton(string label, CommanderTool tool)
        {
            bool selected = _tool == tool;
            if (selected)
            {
                ImGui.PushStyleColor(ImGuiCol.Button, new System.Numerics.Vector4(0.25f, 0.55f, 0.3f, 1f));
            }

            if (ImGui.Button(label))
            {
                _tool = tool;
            }

            if (selected)
            {
                ImGui.PopStyleColor();
            }
        }

        static void Row(string name, int value)
        {
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.Text(name);
            ImGui.TableSetColumnIndex(1);
            ImGui.Text(value.ToString(CultureInfo.InvariantCulture));
        }
    }

    /// <summary>
    /// Jméno druhu do panelu, bez jmenného prostoru.
    /// </summary>
    /// <remarks>
    /// Bez diakritiky, protože výchozí font ImGui umí znaky jen do 0xFF (docs/STAV.md).
    /// Jména bloků jsou stejně anglická, takže je to jen pojistka pro obsah zvenku.
    /// </remarks>
    private string ShortItemName(ushort item)
    {
        if (_world is null || item >= _world.Registry.Count)
        {
            return item.ToString(CultureInfo.InvariantCulture);
        }

        string id = _world.Registry.Definition(item).Id;
        int colon = id.LastIndexOf(':');
        return colon >= 0 ? id[(colon + 1)..] : id;
    }

    /// <summary>
    /// Vykreslí panel obrazu a barev.
    /// </summary>
    /// <remarks>
    /// Kreslí se vpravo, aby nepřekrýval F3 overlay ani vývojářské menu vlevo. Když se
    /// nevejde, ustoupí doleva až k okraji — na užším okně je lepší přesah přes overlay
    /// než uříznuté hodnoty za hranou obrazovky.
    /// </remarks>
    private void DrawLookMenu()
    {
        if (_text is null || !_lookMenu.Visible)
        {
            return;
        }

        const int Scale = 2;
        const int LabelWidth = 20;
        const int BarWidth = 10;

        // Kurzor, mezera, popisek, mezera, "-[", pruh, "]+", mezera a hodnota na 7 znaků.
        const int Columns = 2 + LabelWidth + 1 + 2 + BarWidth + 2 + 1 + 7;
        int width = Columns * TextRenderer.AdvanceX * Scale;
        int x = Math.Max(8, ClientSize.X - width - 16);
        int y = 8;

        _text.DrawText("-- OBRAZ A BARVY (F9) --", x, y, Scale);
        y += TextRenderer.LineHeight * Scale;
        _text.DrawText("sipky nahoru/dolu vyber, doleva/doprava = -/+", x, y, 1);
        y += TextRenderer.LineHeight;
        _text.DrawText("Shift = desetkrat vetsi krok, Delete = vychozi hodnoty", x, y, 1);
        y += TextRenderer.LineHeight * Scale;

        foreach (string line in _lookMenu.SliderLines(LabelWidth, BarWidth))
        {
            _text.DrawText(line, x, y, Scale);
            y += TextRenderer.LineHeight * Scale;
        }
    }

    /// <summary>
    /// Poskládá panel obrazu a barev. Řádky sahají přímo na renderer i na uložená
    /// nastavení, takže se změna projeví hned a zároveň přežije restart.
    /// </summary>
    private void BuildLookMenu()
    {
        if (_chunkRenderer is null)
        {
            return;
        }

        ChunkRenderer renderer = _chunkRenderer;

        _lookMenu.AddReadOnly("--- barvy ---", () => string.Empty);

        // HLAVNÍ VYPÍNAČ NAHOŘE. Vypnutím se celá korekce přeskočí a scéna se ukáže tak,
        // jak vyšla z osvětlení — což je nejrychlejší způsob, jak zjistit, jestli za
        // nějakou barevnou ošklivostí stojí korekce, nebo shadery pod ní.
        _lookMenu.AddToggle(
            "Barevna korekce",
            () => renderer.CinematicColorGrading,
            v => { renderer.CinematicColorGrading = v; _playerOptions.ColorGrading = v; });

        _lookMenu.AddFloat(
            "Jas (expozice)",
            () => renderer.Exposure,
            v => { renderer.Exposure = v; _playerOptions.Exposure = v; },
            0.02f, PlayerOptions.MinExposure, PlayerOptions.MaxExposure);

        _lookMenu.AddFloat(
            "Sytost",
            () => renderer.ColorSaturation,
            v => { renderer.ColorSaturation = v; _playerOptions.ColorSaturation = v; },
            0.02f, PlayerOptions.MinSaturation, PlayerOptions.MaxSaturation);

        _lookMenu.AddFloat(
            "Kontrast",
            () => renderer.ColorContrast,
            v => { renderer.ColorContrast = v; _playerOptions.ColorContrast = v; },
            0.02f, PlayerOptions.MinContrast, PlayerOptions.MaxContrast);

        // Rozdělení odstínu je to, co dělá „filmový" dojem — světla do tepla, stíny do
        // modra. Sytost zvedne všechny barvy stejně, tohle je rozejde od sebe.
        _lookMenu.AddFloat(
            "Teplo svetel/stinu",
            () => renderer.SplitTone,
            v => { renderer.SplitTone = v; _playerOptions.SplitTone = v; },
            0.05f, PlayerOptions.MinSplitTone, PlayerOptions.MaxSplitTone);

        _lookMenu.AddReadOnly("--- noc ---", () => string.Empty);

        // JEDNO ČÍSLO PRO CELOU NOC. Všechny shadery terénu, rostlin i vody násobí výsledek
        // hodnotou Moonlight, takže se změna projeví všude naráz.
        _lookMenu.AddFloat(
            "Videt v noci",
            () => renderer.Day.NightBrightness,
            v => { renderer.Day.NightBrightness = v; _playerOptions.NightBrightness = v; },
            0.02f, 0f, 1f);

        // Strop: co znamená plný noční jas, tedy kam až „Videt v noci" na jedničce sahá.
        // Totéž co příkaz /nightmax.
        _lookMenu.AddFloat(
            "Strop noci (nightmax)",
            () => renderer.Day.NightBrightnessCeiling,
            v => { renderer.Day.NightBrightnessCeiling = v; _playerOptions.NightBrightnessCeiling = v; },
            0.05f, 0.05f, 1f);

        // Denní doba je tu proto, aby se noční hodnoty daly nastavit v noci. Bez ní by se
        // na noc muselo čekat, a naslepo se noční jas ladit nedá.
        _lookMenu.AddFloat(
            "Denni doba",
            () => renderer.Day.TimeOfDay,
            v => renderer.Day.TimeOfDay = v - MathF.Floor(v),
            0.02f, 0f, 1f);

        _lookMenu.AddToggle("Cas bezi", () => renderer.Day.Running, v => renderer.Day.Running = v);

        _lookMenu.AddReadOnly("--- zare ---", () => string.Empty);

        _lookMenu.AddToggle(
            "Zare (bloom)",
            () => renderer.Bloom,
            v => { renderer.Bloom = v; _playerOptions.Bloom = v; });

        _lookMenu.AddFloat(
            "Zare - prah",
            () => renderer.BloomThreshold,
            v => { renderer.BloomThreshold = v; _playerOptions.BloomThreshold = v; },
            0.05f, PlayerOptions.MinBloomThreshold, PlayerOptions.MaxBloomThreshold);

        _lookMenu.AddFloat(
            "Zare - sila",
            () => renderer.BloomStrength,
            v => { renderer.BloomStrength = v; _playerOptions.BloomStrength = v; },
            0.05f, PlayerOptions.MinBloomStrength, PlayerOptions.MaxBloomStrength);

        _lookMenu.AddFloat(
            "Zare - polomer",
            () => renderer.BloomRadius,
            v => { renderer.BloomRadius = v; _playerOptions.BloomRadius = v; },
            0.5f, PlayerOptions.MinBloomRadius, PlayerOptions.MaxBloomRadius);

        _lookMenu.AddReadOnly("--- hrany ---", () => string.Empty);

        _lookMenu.AddToggle(
            "Vyhlazeni hran (FXAA)",
            () => renderer.Fxaa,
            v => { renderer.Fxaa = v; _playerOptions.Fxaa = v; });

        _lookMenu.AddToggle(
            "Odrazy vody",
            () => renderer.FancyWater,
            v => { renderer.FancyWater = v; _playerOptions.FancyWater = v; });
    }

    /// <summary>
    /// Přenese uložené nastavení obrazu do rendereru.
    /// </summary>
    /// <remarks>
    /// Volá se při startu. Bez toho by se panel F9 otevřel s výchozími hodnotami
    /// rendereru a naladěné barvy z minulého sezení by se ztratily — přesně to se stalo
    /// s odrazy vody, než se přesunuly do ukládaného nastavení.
    /// </remarks>
    private void ApplyLookOptions()
    {
        if (_chunkRenderer is null)
        {
            return;
        }

        ChunkRenderer renderer = _chunkRenderer;
        renderer.CinematicColorGrading = _playerOptions.ColorGrading;
        renderer.Exposure = _playerOptions.Exposure;
        renderer.ColorSaturation = _playerOptions.ColorSaturation;
        renderer.ColorContrast = _playerOptions.ColorContrast;
        renderer.SplitTone = _playerOptions.SplitTone;
        renderer.BloomThreshold = _playerOptions.BloomThreshold;
        renderer.BloomStrength = _playerOptions.BloomStrength;
        renderer.BloomRadius = _playerOptions.BloomRadius;
    }

    /// <summary>Vrátí obraz na výchozí hodnoty. Odpovídá klávese Delete v panelu.</summary>
    private void ResetLookToDefaults()
    {
        if (_chunkRenderer is null)
        {
            return;
        }

        // Zdroj výchozích hodnot je čerstvý PlayerOptions, ne čísla opsaná sem. Kdyby se
        // opsala, rozešla by se s výchozími hodnotami při první změně na jednom místě.
        var defaults = new PlayerOptions();
        ChunkRenderer renderer = _chunkRenderer;

        renderer.CinematicColorGrading = _playerOptions.ColorGrading = defaults.ColorGrading;
        renderer.Exposure = _playerOptions.Exposure = defaults.Exposure;
        renderer.ColorSaturation = _playerOptions.ColorSaturation = defaults.ColorSaturation;
        renderer.ColorContrast = _playerOptions.ColorContrast = defaults.ColorContrast;
        renderer.SplitTone = _playerOptions.SplitTone = defaults.SplitTone;
        renderer.BloomThreshold = _playerOptions.BloomThreshold = defaults.BloomThreshold;
        renderer.BloomStrength = _playerOptions.BloomStrength = defaults.BloomStrength;
        renderer.BloomRadius = _playerOptions.BloomRadius = defaults.BloomRadius;
        renderer.Day.NightBrightness = _playerOptions.NightBrightness = defaults.NightBrightness;
        renderer.Day.NightBrightnessCeiling = _playerOptions.NightBrightnessCeiling = defaults.NightBrightnessCeiling;

        Log.Info("Obraz vracen na vychozi hodnoty.");
    }

    /// <summary>
    /// Poskládá vývojářské menu. Každý řádek sahá přímo na to, co ladí, takže se nikde
    /// nedrží kopie hodnoty, která by se mohla rozejít se skutečností.
    /// </summary>
    private void BuildDebugMenu()
    {
        if (_streamer is null || _chunkRenderer is null)
        {
            return;
        }

        ChunkStreamer streamer = _streamer;
        ChunkRenderer renderer = _chunkRenderer;

        // CITLIVOST MYŠI JAKO PRVNÍ ŘÁDEK. Je to jediné nastavení, které si člověk sáhne
        // změnit hned, a hledat ho pod dohledem a mlhou nedává smysl.
        //
        // Zobrazuje se stokrát zvětšená: v surových stupních na dílek je to 0,03, což se
        // na posuvníku po setinách nedá rozumně číst ani nastavit.
        _menu.AddFloat(
            "Citlivost mysi",
            () => _mouseSensitivity * 100f,
            v => _mouseSensitivity = v / 100f,
            0.5f, 0.5f, 30f);

        // Se změnou dohledu se posune i mlha. Kdyby zůstala, terén by buď končil ostrou
        // hranou (mlha dál než dohled), nebo by mizel dřív, než ho dohled ořízne.
        _menu.AddInt(
            "Dohled (chunku)",
            () => streamer.ViewDistanceChunks,
            v => streamer.ViewDistanceChunks = v,
            1, 2, 32);
        _menu.AddInt("Svisly dohled", () => streamer.VerticalMeshRadius, v => streamer.VerticalMeshRadius = v, 1, 0, 32);
        _menu.AddFloat(
            "Mlha od",
            () => _fogTuning.Start,
            _fogTuning.SetStart,
            8f, 0f, FogTuning.AbsoluteMaximumDistance - FogTuning.MinimumSpan);
        _menu.AddFloat(
            "Mlha do",
            () => _fogTuning.End,
            _fogTuning.SetEnd,
            8f, FogTuning.MinimumSpan, FogTuning.AbsoluteMaximumDistance);
        _menu.AddInt("Meshu za frame", () => streamer.MeshesPerFrame, v => streamer.MeshesPerFrame = v, 2, 1, 256);
        _menu.AddInt("Meshu naraz", () => streamer.MaxMeshingInFlight, v => streamer.MaxMeshingInFlight = v, 4, 1, 512);
        _menu.AddInt("Generovani za frame", () => streamer.GenerationsPerFrame, v => streamer.GenerationsPerFrame = v, 8, 1, 1024);
        // Dohled řídí, kam až sahá PLNÁ geometrie chunků — a s ní jediné, co umí kreslit
        // trávu, kapradí a kytky. Za ním začíná LOD, který výřezovou vrstvu nemá, takže
        // tam vegetace končí. Zvětšením jde posunout dál, ale plocha roste se čtvercem:
        // z 12 na 24 je čtyřnásobek chunků k nabrání, zameshování i nakreslení.
        _menu.AddInt("Nahrani za frame", () => streamer.UploadsPerFrame, v => streamer.UploadsPerFrame = v, 1, 1, 64);
        _menu.AddFloat("Rozpocet nahrani ms", () => (float)streamer.UploadBudgetMs, v => streamer.UploadBudgetMs = v, 0.05f, 0.05f, 8f);
        _menu.AddInt("Uvolneni za frame", () => streamer.UnloadsPerFrame, v => streamer.UnloadsPerFrame = v, 4, 1, 512);
        _menu.AddToggle("Vzdaleny teren LOD", () => _farTerrain?.Enabled ?? false, v =>
        {
            if (_farTerrain is not null)
            {
                _farTerrain.Enabled = v;
            }
        });

        // EXPERIMENTALNI: plny vodni shader s odrazy oblohy a Fresnelem. Ve vychozim stavu
        // vypnuty - hladina se bere z atlasu a jen prosviti, protoze pres odrazivou vodu
        // neni poznat, co je pod ni. Vlneni a podvodni pohled zustavaji v obou rezimech.
        _menu.AddFloat(
            "Denni doba",
            () => _chunkRenderer?.Day.TimeOfDay ?? 0f,
            v => { if (_chunkRenderer is not null) { _chunkRenderer.Day.TimeOfDay = v - MathF.Floor(v); } },
            0.02f, 0f, 1f);

        _menu.AddToggle(
            "Cas bezi",
            () => _chunkRenderer?.Day.Running ?? false,
            v => { if (_chunkRenderer is not null) { _chunkRenderer.Day.Running = v; } });

        _menu.AddToggle(
            "Slunce zamknute",
            () => _chunkRenderer?.Day.SunFrozen ?? false,
            v => { if (_chunkRenderer is not null) { _chunkRenderer.Day.SunFrozen = v; } });

        _menu.AddFloat(
            "Rychlost casu",
            () => _chunkRenderer?.Day.Speed ?? 0f,
            v => { if (_chunkRenderer is not null) { _chunkRenderer.Day.Speed = v; } },
            0.5f, 0f, 40f);

        _menu.AddFloat(
            "Rychlost prirody",
            () => _chunkRenderer?.Day.NatureSpeed ?? 1f,
            v => { if (_chunkRenderer is not null) { _chunkRenderer.Day.NatureSpeed = v; } },
            1f, 0f, 64f);

        _menu.AddToggle(
            "Odrazy vody (experimentalni)",
            () => _playerOptions.FancyWater,
            v =>
            {
                _playerOptions.FancyWater = v;
                if (_chunkRenderer is not null) { _chunkRenderer.FancyWater = v; }
            });

        // JEDNO CISLO PRO CELOU NOC. Vsechny shadery terenu, rostlin i vody nasobi vysledek
        // hodnotou Moonlight, takze se zmena projevi vsude naraz a nemuze se rozejit.
        _menu.AddFloat(
            "Videt v noci",
            () => _playerOptions.NightBrightness,
            v =>
            {
                _playerOptions.NightBrightness = v;
                if (_chunkRenderer is not null) { _chunkRenderer.Day.NightBrightness = v; }
            },
            0.02f, 0f, 1f);

        _menu.AddToggle(
            "Zare (bloom)",
            () => _chunkRenderer?.Bloom ?? false,
            v => { if (_chunkRenderer is not null) { _chunkRenderer.Bloom = v; } });

        _menu.AddFloat(
            "Zare - prah",
            () => _chunkRenderer?.BloomThreshold ?? 0f,
            v => { if (_chunkRenderer is not null) { _chunkRenderer.BloomThreshold = v; } },
            0.05f, 0f, 1f);

        _menu.AddFloat(
            "Zare - sila",
            () => _chunkRenderer?.BloomStrength ?? 0f,
            v => { if (_chunkRenderer is not null) { _chunkRenderer.BloomStrength = v; } },
            0.05f, 0f, 2f);

        _menu.AddFloat(
            "Zare - polomer",
            () => _chunkRenderer?.BloomRadius ?? 0f,
            v => { if (_chunkRenderer is not null) { _chunkRenderer.BloomRadius = v; } },
            0.5f, 1f, 20f);
        _menu.AddInt("LOD pasem (dosah)", () => _farTerrain?.ActiveLevels ?? 0,
            v => { if (_farTerrain is not null) { _farTerrain.ActiveLevels = v; } }, 1, 1, FarTerrain.MaxLevels);
        _menu.AddInt("LOD dlazdic za frame", () => _farTerrain?.BuildsPerFrame ?? 0,
            v => { if (_farTerrain is not null) { _farTerrain.BuildsPerFrame = v; } }, 1, 1, 64);

        // Kolik pásem LOD dostane skutečné stromy. Nula = les jen barvou, každé pásmo
        // navíc posune stromy dál, ale stojí geometrii.
        _menu.AddInt("LOD pasem se stromy", () => FarTerrain.TreeLevels,
            v => FarTerrain.TreeLevels = v, 1, 0, FarTerrain.MaxLevels);

        // Rostliny v LOD. Nula je vypne a vegetace pak končí na hranici chunků jako dřív —
        // slouží to i k rozhodnutí, jestli šum na obzoru dělají ony, nebo něco jiného.
        _menu.AddInt("LOD pasem s travou", () => FarTerrain.PlantLevels,
            v => FarTerrain.PlantLevels = v, 1, 0, FarTerrain.MaxLevels);
        _menu.AddReadOnly("LOD dosah (bloku)", () => (_farTerrain?.OuterRadius ?? 0).ToString(CultureInfo.InvariantCulture));
        _menu.AddToggle("Vsync (ceka na monitor)", () => _vsync, v =>
        {
            _vsync = v;
            ApplyVsync();
        });

        _menu.AddToggle("Backface culling", () => renderer.BackfaceCulling, v => renderer.BackfaceCulling = v);
        _menu.AddToggle("Pruchod zdmi (F)", () => _player.NoClip, v => _player.NoClip = v);

        // ČAS GRAFIKY PO PRŮCHODECH.
        //
        // Čas hlavního vlákna o tomhle neříká nic: kreslení je jen zápis příkazů, který
        // trvá mikrosekundy bez ohledu na to, jestli grafika pak počítá dvě milisekundy
        // nebo dvacet. Když snímky klesnou a hlavní vlákno má volno, ztrácí se čas tady.
        //
        // Čísla jsou o pár snímků stará — grafika značky vyplní, až na ně dojde řada.
        _menu.AddReadOnly("--- grafika (ms) ---", () => string.Empty);

        // Měření se zapíná ručně a je to nutnost: časová značka nutí grafiku počkat, až
        // všechno doběhne, takže deset značek na snímek rozseká práci na deset kusů, které
        // se nesmějí překrývat. Naměřeno, že se zapnutým měřením spadnou snímky z 650
        // na 120 za vteřinu — čísla se tedy dají číst jen jako POMĚRY mezi průchody.
        _menu.AddToggle(
            "Merit cas grafiky (zpomali!)",
            () => renderer.GpuProfiler.Enabled,
            v => renderer.GpuProfiler.Enabled = v);

        if (renderer.GpuProfiler.Available)
        {
            for (int i = 0; i < renderer.GpuProfiler.SectionCount; i++)
            {
                int section = i;
                _menu.AddReadOnly(
                    renderer.GpuProfiler.Name(section),
                    () => renderer.GpuProfiler.Milliseconds(section).ToString("F3", CultureInfo.InvariantCulture));
            }

            _menu.AddReadOnly(
                "CELKEM grafika",
                () => renderer.GpuProfiler.Total.ToString("F3", CultureInfo.InvariantCulture));
        }
        else
        {
            _menu.AddReadOnly("(zarizeni neumi casove znacky)", () => string.Empty);
        }

        // ROZPAD SIMULAČNÍHO TIKU. Kritérium T1: na obrazovce musí být vidět, kolik ms bere
        // který druh práce. Kategorie jsou pevné dopředu, i když pathfinding a pásy zatím
        // neexistují — nula u nich je informace, ne chyba.
        _menu.AddReadOnly("--- tick (ms) ---", () => string.Empty);
        _menu.AddReadOnly("Tiku za frame", () => _ticksLastFrame.ToString(CultureInfo.InvariantCulture));
        _menu.AddReadOnly("Tick p50 / p99", () => string.Create(
            CultureInfo.InvariantCulture,
            $"{TickTotalStats().P50Ms:F3} / {TickTotalStats().P99Ms:F3}"));

        // Bez poslední kategorie: "celkem" se ukazuje výš i s p99, tady by se opakovalo.
        for (int i = 0; i < TickPhaseTotal; i++)
        {
            int phase = i;
            _menu.AddReadOnly(
                "  " + _tickPhases.Name(phase),
                () => _tickPhases.Stats(phase).P50Ms.ToString("F3", CultureInfo.InvariantCulture));
        }

        // KOLONIE. „Volnych lidi" je to číslo, na kterém stojí
        // celá hra — proto je první.
        _menu.AddReadOnly("--- kolonie ---", () => string.Empty);
        _menu.AddReadOnly("VOLNYCH LIDI", () => string.Create(
            CultureInfo.InvariantCulture,
            $"{_colony.FreeColonists} z {_colony.Colonists.Count}"));
        _menu.AddReadOnly("Uvolnila automatizace", () =>
            _colony.FreedByAutomation.ToString(CultureInfo.InvariantCulture));
        _menu.AddReadOnly("Prace (volna/dela se/hotovo)", () => string.Create(
            CultureInfo.InvariantCulture,
            $"{_colony.Jobs.OpenCount} / {_colony.Jobs.ClaimedCount} / {_colony.Jobs.DoneCount}"));
        _menu.AddReadOnly("Odlozena prace", () =>
            _colony.Jobs.DeferredCount.ToString(CultureInfo.InvariantCulture));
        _menu.AddReadOnly("Navigace (hotovo/ceka)", () => string.Create(
            CultureInfo.InvariantCulture,
            $"{_colony.NavigationChunks} / {_colony.PendingNavigation}"));
        _menu.AddReadOnly("Stroju (aktivnich)", () => string.Create(
            CultureInfo.InvariantCulture,
            $"{_colony.MachineCount} ({_colony.Machines.ActiveCount})"));
        _menu.AddReadOnly("Rezim", () => _commander.Commanding ? "velitel" : "prvni osoba");

        _menu.AddReadOnly("--- namereno ---", () => string.Empty);
        _menu.AddReadOnly("Chunku ve svete", () => _world?.ChunkCount.ToString(CultureInfo.InvariantCulture) ?? "?");
        _menu.AddReadOnly("S geometrii", () => renderer.LoadedChunks.ToString(CultureInfo.InvariantCulture));
        _menu.AddReadOnly("Viditelnych", () => renderer.VisibleChunks.ToString(CultureInfo.InvariantCulture));
        _menu.AddReadOnly("LOD dlazdic", () => string.Create(
            CultureInfo.InvariantCulture,
            $"{renderer.FarTiles} ({renderer.VisibleFarTiles} videt, {_farTerrain?.PendingTiles ?? 0} ve fronte)"));
        _menu.AddReadOnly("Trojuhelniku", () => renderer.TriangleCount.ToString("N0", CultureInfo.InvariantCulture));
        _menu.AddReadOnly("Fronta meshingu", () => streamer.PendingMeshing.ToString(CultureInfo.InvariantCulture));

        // Rozepsané schválně. Jediné číslo, které znamená chybu, je to první; ostatní tři
        // popisují stavy, které se samy vyřeší. Dřív se počítaly dohromady a výsledek
        // ukazoval stovky i ve chvíli, kdy bylo všechno v pořádku.
        _menu.AddReadOnly("ZTRACENE (chyba!)", () => streamer.LostChunks.ToString(CultureInfo.InvariantCulture));
        _menu.AddReadOnly("  ceka na sousedy", () => streamer.WaitingChunks.ToString(CultureInfo.InvariantCulture));
        _menu.AddReadOnly("  mimo slupku", () => streamer.SkippedChunks.ToString(CultureInfo.InvariantCulture));
        _menu.AddReadOnly("  rozpracovanych", () => streamer.PendingChunks.ToString(CultureInfo.InvariantCulture));
        _menu.AddReadOnly("LOD odlozenych", () => (_farTerrain?.RetiredTiles ?? 0).ToString(CultureInfo.InvariantCulture));

        // Když je „svet hotovy do" výrazně míň než „LOD od", streaming nestíhá a LOD
        // právě zaskakuje dovnitř. Když se ta dvě čísla rovnají, svět je nabraný celý.
        _menu.AddReadOnly("Svet hotovy do", () => string.Create(
            CultureInfo.InvariantCulture, $"{streamer.CompleteRadiusBlocks:F0} bloku"));
        _menu.AddReadOnly("LOD zacina od", () => string.Create(
            CultureInfo.InvariantCulture, $"{_farTerrain?.NearRadius ?? 0f:F0} bloku"));
        _menu.AddReadOnly("Fronta generovani", () => streamer.PendingGeneration.ToString(CultureInfo.InvariantCulture));
        _menu.AddReadOnly("Ziva vegetace", () => _ecology is null
            ? "vypnuto"
            : string.Create(
                CultureInfo.InvariantCulture,
                $"{_ecology.KnownTrees} stromu, +{_ecology.SeededTrees} ({_ecology.ExpandedTrees} rozs.), -{_ecology.FallenTrees}, {_ecology.PlantChanges} zmen"));
        _menu.AddReadOnly("Frame ms (p50/p99)", () => FrameSummary());

        // Kolik z framu je práce a kolik čekání. Se zapnutým vsyncem je čekání většina
        // a FPS pak ukazuje frekvenci monitoru, ne to, co engine zvládne.
        _menu.AddReadOnly("Prace / cekani ms", () => WorkVersusWait());

        // Ukládání. „Nacteno" nenulové znamená, že se svět vrátil z disku místo
        // z generátoru — tedy že to, co jsi postavil, přežilo.
        _menu.AddReadOnly("Ulozeno / nacteno", () => _storage is null
            ? "vypnuto"
            : string.Create(
                CultureInfo.InvariantCulture,
                $"{_storage.SavedChunks} / {_storage.LoadedChunks} ({_storage.PendingWrites} ceka)"));
    }

    /// <summary>
    /// Rozdělí typický snímek na práci a na čekání.
    ///
    /// <para>Práce je streaming, vzdálený terén, vydávání kreslení a overlay. Čekání je
    /// fronta na grafiku a na volný obraz swapchainu — se zapnutým vsyncem je v tom
    /// schovaná i doba do další obnovy monitoru.</para>
    /// </summary>
    private string WorkVersusWait()
    {
        double work =
            _phases.Stats(PhaseStreaming).P50Ms
            + _phases.Stats(PhaseFarTerrain).P50Ms
            + _phases.Stats(PhaseDraw).P50Ms
            + _phases.Stats(PhaseOverlay).P50Ms;

        double wait = _phases.Stats(PhaseWaitGpu).P50Ms + _phases.Stats(PhasePresent).P50Ms;

        return string.Create(CultureInfo.InvariantCulture, $"{work:F2} / {wait:F2}");
    }

    private string FrameSummary()
    {
        FrameStats stats = _frameTimer.TotalStats();
        return string.Create(CultureInfo.InvariantCulture, $"{stats.P50Ms:F1} / {stats.P99Ms:F1}");
    }

    /// <summary>Klávesy menu. Řeší se hranou stisku, aby jedno zmáčknutí byl jeden krok.</summary>
    private void HandleDebugMenuKeys(KeyboardState keyboard)
    {
        // Dvě klávesy schválně: F4 si na některých systémech bere okenní správce
        // (Alt+F4 a spol.) a nemusí do hry vůbec dojít.
        bool f4 = keyboard.IsKeyDown(Keys.F4) || keyboard.IsKeyDown(Keys.F1);
        if (f4 && !_f4WasDown)
        {
            _menu.Visible = !_menu.Visible;

            // OBĚ MENU NARÁZ NE. Přetahovala by se o šipky a překrývala by se na obraze,
            // takže otevření jednoho zavře druhé.
            if (_menu.Visible)
            {
                CloseLookMenu();
            }
        }

        _f4WasDown = f4;

        // REŽIM VELITELE. Tab schválně: je pod prstem a nekoliduje s ničím ve hře.
        bool tab = keyboard.IsKeyDown(Keys.Tab);
        if (tab && !_tabWasDown && _startupStage == StartupStage.Playing && !_pauseMenuOpen)
        {
            ToggleCommanderMode();
        }

        _tabWasDown = tab;

        bool f9 = keyboard.IsKeyDown(Keys.F9);
        if (f9 && !_f9WasDown)
        {
            if (_lookMenu.Visible)
            {
                CloseLookMenu();
            }
            else
            {
                _lookMenu.Visible = true;
                _menu.Visible = false;
            }
        }

        _f9WasDown = f9;

        // Klávesy obsluhují to menu, které je zrovna vidět. Panel obrazu má přednost:
        // otevřít se dá jen samostatně, takže tahle větev nastane jen tehdy, když je nahoře.
        DebugMenu? active = _lookMenu.Visible ? _lookMenu : _menu.Visible ? _menu : null;
        if (active is null)
        {
            return;
        }

        int multiplier = keyboard.IsKeyDown(Keys.LeftShift) ? 10 : 1;

        bool up = keyboard.IsKeyDown(Keys.Up);
        bool down = keyboard.IsKeyDown(Keys.Down);
        bool left = keyboard.IsKeyDown(Keys.Left);
        bool right = keyboard.IsKeyDown(Keys.Right);

        if (up && !_menuUpWasDown)
        {
            active.Move(-1);
        }

        if (down && !_menuDownWasDown)
        {
            active.Move(1);
        }

        if (left && !_menuLeftWasDown)
        {
            active.Adjust(-1, multiplier);
        }

        if (right && !_menuRightWasDown)
        {
            active.Adjust(1, multiplier);
        }

        _menuUpWasDown = up;
        _menuDownWasDown = down;
        _menuLeftWasDown = left;
        _menuRightWasDown = right;

        // NÁVRAT NA VÝCHOZÍ JE NA DELETE, NE NA R. R ve hře přepíná nástroj tesání a to se
        // vyhodnocuje jinde v HandleToggles — na obojí naráz by jedno zmáčknutí udělalo
        // dvě různé věci.
        bool reset = keyboard.IsKeyDown(Keys.Delete);
        if (reset && !_menuResetWasDown && ReferenceEquals(active, _lookMenu))
        {
            ResetLookToDefaults();
        }

        _menuResetWasDown = reset;
    }

    /// <summary>Zavře panel obrazu a uloží naladěné hodnoty.</summary>
    /// <remarks>
    /// Ukládá se při zavření, ne při každém kroku posuvníku: jedno podržení šipky je
    /// desítky kroků za vteřinu a každý by znamenal zápis souboru na disk.
    /// </remarks>
    private void CloseLookMenu()
    {
        if (!_lookMenu.Visible)
        {
            return;
        }

        _lookMenu.Visible = false;
        try
        {
            _playerOptions.Save();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Neuložené nastavení není důvod k pádu uprostřed hry. Zůstane platné do konce
            // sezení a hráč se dozví proč.
            Log.Warn($"Nastaveni obrazu se nepodarilo ulozit: {exception.Message}");
        }
    }

    // ================= SONDA NA BLIKÁNÍ =================
    //
    // Měří, o kolik se obraz liší od předchozího snímku, a hlavně KDE. Průměr přes plochu
    // zanikne. Proto vzniká i obrázek nejhorší změny, na kterém je vidět, co konkrétně
    // problikává.
    //
    // se pozná, jestli blikání sedí na hranice epoch, nebo je rozprostřené.
    //
    // Spouští se klávesou F9. Ukládá dva obrázky vedle binárky: co bylo vidět a co z toho
    // problikávalo.
    private const int FlickerWidth = 480;
    private const int FlickerHeight = 320;

    /// <summary>Kolik snímků nahraje jeden stisk F9.</summary>
    private const int FlickerManualFrames = 180;

    private void StartFlickerRecording()
    {
        _flickerRecording = FlickerManualFrames;
        _flickerSamples.Clear();
        _flickerPrevious = null;
        _flickerWorst = null;

        Log.Info($"SONDA BLIKANI: nahravam {FlickerManualFrames} snimku, kamerou ani mysi nehybej.");
    }

    private void RequestFlickerCapture()
    {
        if (_renderer is null || _captureThisFrame || _flickerRecording <= 0)
        {
            return;
        }

        if (_flickerSamples.Count >= _flickerRecording)
        {
            _flickerRecording = 0;
            ReportFlicker();
            return;
        }

        int x = ((int)_swapchain!.Extent.Width - FlickerWidth) / 2;
        int y = ((int)_swapchain!.Extent.Height - FlickerHeight) / 2;

        _renderer.RequestCapture(Math.Max(0, x), Math.Max(0, y), FlickerWidth, FlickerHeight);
        _flickerCapture = true;
    }

    private void ConsumeFlickerCapture()
    {
        if (!_flickerCapture || _renderer is null)
        {
            return;
        }

        _flickerCapture = false;

        byte[]? pixels = _renderer.TakeCapture();
        if (pixels is null)
        {
            return;
        }

        if (_flickerPrevious is not null && _flickerPrevious.Length == pixels.Length)
        {
            long sum = 0;
            long brightness = 0;

            for (int i = 0; i < pixels.Length; i += 4)
            {
                sum += Math.Abs(pixels[i] - _flickerPrevious[i]);
                sum += Math.Abs(pixels[i + 1] - _flickerPrevious[i + 1]);
                sum += Math.Abs(pixels[i + 2] - _flickerPrevious[i + 2]);

                brightness += pixels[i] + pixels[i + 1] + pixels[i + 2];
            }

            double samples = pixels.Length / 4 * 3;

            _flickerSamples.Add((sum / samples, false));
            _flickerBrightness = brightness / samples;

            // Nejvetsi zmena mezi dvema snimky, kterou kazdy pixel za celou dobu zazil.
            // Ukaze, KDE se blika — prumer pres celou plochu to zredi do neviditelna.
            _flickerWorst ??= new byte[pixels.Length];

            for (int i = 0; i < pixels.Length; i += 4)
            {
                int delta = Math.Max(
                    Math.Abs(pixels[i] - _flickerPrevious[i]),
                    Math.Max(
                        Math.Abs(pixels[i + 1] - _flickerPrevious[i + 1]),
                        Math.Abs(pixels[i + 2] - _flickerPrevious[i + 2])));

                byte scaled = (byte)Math.Min(255, delta * 12);

                if (scaled > _flickerWorst[i])
                {
                    _flickerWorst[i] = scaled;
                    _flickerWorst[i + 1] = scaled;
                    _flickerWorst[i + 2] = scaled;
                    _flickerWorst[i + 3] = 255;
                }
            }
        }

        _flickerPrevious = pixels;
    }

    private void ReportFlicker()
    {
        double[] all = [.. _flickerSamples.Select(s => s.Diff).Order()];

        double[] onEpoch = [.. _flickerSamples.Where(s => s.Epoch).Select(s => s.Diff)];
        double[] between = [.. _flickerSamples.Where(s => !s.Epoch).Select(s => s.Diff)];

        Log.Info($"FLICKER vzorku {all.Length}, na hranici epochy {onEpoch.Length}, jas {_flickerBrightness:F1}, denni doba {_chunkRenderer?.Day.TimeOfDay:F4}, cas bezi {_chunkRenderer?.Day.Running}");
        Log.Info($"FLICKER rozdil snimku: p50 {all[all.Length / 2]:F3}  p95 {all[all.Length * 95 / 100]:F3}  max {all[^1]:F3}");
        Log.Info($"FLICKER na hranici epochy prumer {(onEpoch.Length > 0 ? onEpoch.Average() : 0):F3}, mezi nimi {(between.Length > 0 ? between.Average() : 0):F3}");

        // Casova rada, at je videt perioda. Kazdy radek je 40 snimku.
        for (int start = 0; start < Math.Min(_flickerSamples.Count, 320); start += 40)
        {
            string line = string.Join(
                ' ',
                _flickerSamples.Skip(start).Take(40).Select(s => (s.Epoch ? "*" : "") + s.Diff.ToString("F1")));

            Log.Info($"FLICKER rada {start,4}: {line}");
        }

        const string tag = "blikani";

        if (_flickerPrevious is not null)
        {
            Screenshot.WriteBmp($"{tag}-pohled.bmp", _flickerPrevious, FlickerWidth, FlickerHeight);
        }

        if (_flickerWorst is not null)
        {
            Screenshot.WriteBmp($"{tag}-zmena.bmp", _flickerWorst, FlickerWidth, FlickerHeight);
        }

        Log.Info($"SONDA BLIKANI: ulozeno {tag}-pohled.bmp a {tag}-zmena.bmp do {Environment.CurrentDirectory}");
    }

    /// <summary>
    /// Přihlásí se o přečtení obrazovky, pokud tenhle snímek patří do důkazů selftestu.
    /// Volá se před odesláním snímku.
    /// </summary>
    private void RequestSelftestCapture()
    {
        _captureThisFrame = false;

        if (_selftestFrames <= 0 || _renderer is null)
        {
            return;
        }

        bool wantsScene = _frameTimer.FrameCount == _selftestFrames - 2;
        bool wantsProof = _frameTimer.FrameCount >= _selftestFrames - 1 && OverlayProof is null;

        if (!wantsScene && !wantsProof)
        {
            return;
        }

        // Čte se vždycky celá obrazovka, i když důkaz o overlayi potřebuje jen proužek
        // vlevo nahoře. Vulkan totiž dovolí jednu kopii na snímek a v posledním snímku
        // se potřebuje obojí — maska scény i overlay.
        _renderer.RequestCapture(0, 0, (int)_swapchain!.Extent.Width, (int)_swapchain!.Extent.Height);
        _captureThisFrame = true;
    }

    /// <summary>Zpracuje přečtený snímek. Volá se po odeslání, kdy jsou data hotová.</summary>
    private void ConsumeSelftestCapture()
    {
        if (!_captureThisFrame || _renderer is null)
        {
            return;
        }

        _captureThisFrame = false;

        byte[]? pixels = _renderer.TakeCapture();
        if (pixels is null)
        {
            return;
        }

        int width = (int)_swapchain!.Extent.Width;
        int height = (int)_swapchain!.Extent.Height;

        if (_frameTimer.FrameCount == _selftestFrames - 2)
        {
            _sceneWithoutCulling = SampleScene(pixels, width, height);
            return;
        }

        if (_frameTimer.FrameCount >= _selftestFrames - 1 && OverlayProof is null)
        {
            _sceneWithCulling = SampleScene(pixels, width, height);
            CompareCullingModes();
            CaptureOverlayProof(pixels, width, height);
            RunRaycastProbe();
            RecordWorldStatistics();

            SaveScreenshot(pixels, width, height);
        }
    }

    /// <summary>
    /// Uloží poslední snímek vedle binárky.
    ///
    /// Vzniklo to poté, co se ukázalo, že hra může projít jedenácti kritérii a přitom
    /// kreslit obraz s obrácenými stěnami. Čísla to neodhalí, obrázek ano.
    /// </summary>
    private void SaveScreenshot(byte[] pixels, int width, int height)
    {
        try
        {
            ScreenshotPath = Path.Combine(AppContext.BaseDirectory, "tesseris-selftest.bmp");
            Screenshot.WriteBmp(ScreenshotPath, pixels, width, height);
        }
        catch (IOException ex)
        {
            Log.Warn($"Snímek se nepodařilo uložit: {ex.Message}");
            ScreenshotPath = null;
        }
    }

    /// <summary>Kam se uložil snímek z posledního framu selftestu.</summary>
    public string? ScreenshotPath { get; private set; }

    private void RecordWorldStatistics()
    {
        LoadedChunks = _world?.ChunkCount ?? 0;
        RenderedChunks = _chunkRenderer?.LoadedChunks ?? 0;
        TriangleCount = _chunkRenderer?.TriangleCount ?? 0;

        long allocated = GC.GetTotalAllocatedBytes(precise: false) - _allocatedAtStart;
        double allocatedPerFrame = _frameTimer.FrameCount > 0
            ? allocated / (double)_frameTimer.FrameCount
            : 0.0;

        GcSummary = string.Create(
            CultureInfo.InvariantCulture,
            $"gen0 {GC.CollectionCount(0) - _gcAtStart[0]}, gen1 {GC.CollectionCount(1) - _gcAtStart[1]}, " +
            $"gen2 {GC.CollectionCount(2) - _gcAtStart[2]} | alokováno {allocated / (1024.0 * 1024.0):F1} MB " +
            $"({allocatedPerFrame / 1024.0:F1} kB na frame)");

        if (_streamer is not null)
        {
            StreamerQueues = string.Create(
                CultureInfo.InvariantCulture,
                $"generování: fronta {_streamer.PendingGeneration}, rozpracováno {_streamer.GeneratingCount} | " +
                $"meshing: fronta {_streamer.PendingMeshing}, rozpracováno {_streamer.MeshingCount}, hotovo {_streamer.UploadedCount}");
        }
    }

    /// <summary>
    /// Podvzorkovaná maska „tady se něco vykreslilo" pro celou obrazovku.
    ///
    /// Data přicházejí v RGBA a s řádky <b>shora dolů</b> (viz <c>VulkanRenderer.TakeCapture</c>).
    /// OpenGL verze četla zdola nahoru, ale na masce ani na jejím porovnání to nic nemění —
    /// obě porovnávané masky vznikají stejně.
    /// </summary>
    private static bool[] SampleScene(byte[] pixels, int width, int height)
    {
        int columns = width / SceneSampleStep;
        int rows = height / SceneSampleStep;
        bool[] mask = new bool[columns * rows];

        for (int row = 0; row < rows; row++)
        {
            for (int column = 0; column < columns; column++)
            {
                int x = column * SceneSampleStep;
                int y = row * SceneSampleStep;
                int offset = ((y * width) + x) * 4;

                mask[(row * columns) + column] = !IsSky(pixels[offset], pixels[offset + 1], pixels[offset + 2]);
            }
        }

        return mask;
    }

    /// <summary>
    /// Je tenhle pixel obloha, tedy místo, kde se nic nevykreslilo?
    ///
    /// <para>
    /// <b>Původní podmínka byla vadná a kvůli tomu bylo celé kritérium na navíjení stěn
    /// bezcenné.</b> Ptala se na <c>R &lt; 150</c>, jenže obloha má R = 158 (naměřeno
    /// přečtením snímku). Za oblohu tedy neoznačila nic, maska vyšla „všude něco je"
    /// v obou režimech a rozdíl mezi cullingem zapnutým a vypnutým vycházel vždycky
    /// kolem nuly — i když se scéna kreslila naruby.
    /// </para>
    /// <para>
    /// Nová podmínka se drží toho, čím je obloha zvláštní: je výrazně modrá. Písek
    /// (152, 144, 110) ani kámen (72, 72, 76) tuhle podmínku nesplní, obloha
    /// (158, 194, 235) ano. Terén v dálce, který mlha prolne do barvy oblohy, se za
    /// oblohu počítat má — je to totéž, jako by tam nic nebylo.
    /// </para>
    /// </summary>
    internal static bool IsSky(byte r, byte g, byte b) => b > 200 && b > r + 40;

    private void CompareCullingModes()
    {
        if (_sceneWithCulling is null || _sceneWithoutCulling is null ||
            _sceneWithCulling.Length != _sceneWithoutCulling.Length)
        {
            return;
        }

        int differing = 0;
        for (int i = 0; i < _sceneWithCulling.Length; i++)
        {
            if (_sceneWithCulling[i] != _sceneWithoutCulling[i])
            {
                differing++;
            }
        }

        CullingDifference = (double)differing / _sceneWithCulling.Length;
    }

    private void RunRaycastProbe()
    {
        if (_world is null)
        {
            return;
        }

        bool found = VoxelRaycast.Cast(
            _camera.Position,
            _camera.Forward,
            maxDistance: 400f,
            _world.IsSolid,
            out VoxelHit hit);

        if (!found)
        {
            RaycastResult = "nic v dosahu 400 m";
            return;
        }

        ushort block = _world.GetBlock(hit.Block.X, hit.Block.Y, hit.Block.Z);
        string id = _world.Registry.Definition(block).Id;

        RaycastResult = string.Create(
            CultureInfo.InvariantCulture,
            $"{id} na [{hit.Block.X}, {hit.Block.Y}, {hit.Block.Z}], normála [{hit.Normal.X}, {hit.Normal.Y}, {hit.Normal.Z}], vzdálenost {hit.Distance:F2}");
    }

    /// <summary>
    /// Spočítá rozsvícené pixely overlaye a vykreslí z nich obrázek do konzole.
    ///
    /// <para>
    /// Proti OpenGL verzi se změnily dvě věci. Zaprvé počátek: Vulkan má řádek 0 nahoře,
    /// takže se nemusí obracet a overlay leží rovnou na začátku dat. Zadruhé měřítko:
    /// na displeji s vyšším rozlišením je swapchain v pixelech větší než okno v bodech,
    /// a text se kreslí v bodech — proto se souřadnice přepočítávají poměrem obojího.
    /// Bez toho by se na Retině četla jen čtvrtina overlaye.
    /// </para>
    /// </summary>
    private void CaptureOverlayProof(byte[] pixels, int frameWidth, int frameHeight)
    {
        float scale = ClientSize.X > 0 ? frameWidth / (float)ClientSize.X : 1f;

        int width = Math.Min((int)(340 * scale), frameWidth);
        int height = Math.Min((int)((8 + (TextRenderer.LineHeight * 4) + 16) * scale), frameHeight);

        bool Lit(int x, int y)
        {
            int offset = ((y * frameWidth) + x) * 4;
            return pixels[offset] > 235 && pixels[offset + 1] > 235 && pixels[offset + 2] > 235;
        }

        int lit = 0;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (Lit(x, y))
                {
                    lit++;
                }
            }
        }

        OverlayLitPixels = lit;

        // Obrázek se vzorkuje v bodech okna přepočtených na pixely, aby vypadal stejně
        // bez ohledu na to, jestli displej pixely zdvojuje.
        int step = Math.Max(1, (int)(2 * scale));

        var art = new System.Text.StringBuilder();
        for (int y = (int)(8 * scale); y < height && y < (int)(24 * scale); y += step)
        {
            for (int x = 0; x < Math.Min((int)(160 * scale), width); x += step)
            {
                art.Append(Lit(x, y) ? '#' : '.');
            }

            art.Append('\n');
        }

        OverlayProof = art.ToString();
    }

    /// <summary>
    /// Escape zavírá, co je otevřené — hru ne.
    /// </summary>
    /// <remarks>
    /// <para><b>Dřív ukončoval hru.</b> To je v hráčském rozhraní past: escape je klávesa,
    /// po které člověk sáhne, když chce zavřít pec nebo inventář, a místo toho mu spadlo
    /// okno. Hra se ukončuje křížkem okna nebo Alt+F4, tedy tím, čím se ukončuje cokoli
    /// jiného.</para>
    ///
    /// <para><b>Pořadí je odshora dolů podle toho, co překrývá co:</b> nejdřív panel
    /// inventáře nebo pece, pak vývojářské menu. Když není otevřené nic, escape pustí
    /// myš — hráč se tím dostane z zachyceného kurzoru ven, aniž by hru zavíral, a dalším
    /// stiskem ji zase chytí.</para>
    /// </remarks>
    private void HandleEscape()
    {
        if (_optionsMenuOpen)
        {
            _optionsMenuOpen = false;
            return;
        }

        if (_pauseMenuOpen)
        {
            ResumeGame();
            return;
        }

        if (_modCommandOpen)
        {
            CloseModCommand();
            return;
        }

        if (_mods?.Client.Ui.CloseActiveScreen() == true
            || _mods?.Screens.CloseActiveScreen() == true)
        {
            return;
        }

        if (_inventoryScreen?.Open == true)
        {
            CloseInventory();
            return;
        }

        if (_menu.Visible)
        {
            _menu.Visible = false;
            return;
        }

        // Nic otevřeného: přepnout zachycení myši.
        OpenPauseMenu();
    }

    private void OpenPauseMenu()
    {
        _pauseMenuOpen = true;
        _optionsMenuOpen = false;
        _pauseLeftWasDown = MouseState.IsButtonDown(MouseButton.Left);
        _mining?.Stop();
        if (_chunkRenderer is not null)
        {
            _chunkRenderer.Held = null;
        }
        CursorState = CursorState.Normal;
    }

    private void ResumeGame()
    {
        _pauseMenuOpen = false;
        _optionsMenuOpen = false;
        CursorState = CursorState.Grabbed;
        _ignoreNextMouseDelta = true;
    }

    /// <summary>
    /// Zavře panel inventáře a uklidí po něm.
    /// </summary>
    /// <remarks>
    /// Sdílí to klávesa E i escape. Kdyby to každá dělala po svém, jedna z nich by dřív nebo
    /// později zapomněla pustit pec nebo znovu chytit myš — a projevilo by se to jako
    /// „po zavření inventáře nejde otáčet".
    /// </remarks>
    private void CloseInventory()
    {
        if (_inventoryScreen is null)
        {
            return;
        }

        if (_inventoryScreen.Chest is not null && _world is not null)
        {
            StartChestAnimation(_openChest, targetOpen: false);
            if (_openChestPartner is { } partner)
            {
                StartChestAnimation(partner, targetOpen: false);
            }
        }

        _inventoryScreen.Open = false;
        _inventoryScreen.Furnace = null;
        _inventoryScreen.Chest = null;
        _inventoryScreen.SearchFocused = false;
        _openChest = default;
        _openChestPartner = null;

        CursorState = CursorState.Grabbed;

        // Po zpětném zachycení přijde skok přes půl obrazovky, viz HandleLook.
        _ignoreNextMouseDelta = true;
    }

    private void HandleInput(float deltaSeconds)
    {
        if (_world is null || _hotbar is null || _streamer is null)
        {
            return;
        }

        KeyboardState keyboard = KeyboardState;

        bool escape = keyboard.IsKeyDown(Keys.Escape);

        if (escape && !_escWasDown)
        {
            HandleEscape();
        }

        _escWasDown = escape;

        if (_pauseMenuOpen)
        {
            HandlePauseMenuInput();
            _mining?.Stop();
            return;
        }

        if (_modCommandOpen)
        {
            _mining?.Stop();
            return;
        }

        if (HasActiveModScreen)
        {
            DispatchModScreenPointer();
            _mining?.Stop();
            return;
        }

        bool typingInInventorySearch = _inventoryScreen?.Open == true
            && _inventoryScreen.SearchFocused;

        if (!typingInInventorySearch)
        {
            // VELITEL SE MYŠÍ NEROZHLÍŽÍ. Kurzorem ukazuje do světa; kdyby se u toho zároveň
            // otáčela hlava postavy, mířilo by se pokaždé jinam a v první osobě by se pak
            // člověk koukal někam, kam se nikdy nepodíval.
            //
            // PODMÍNKA PATŘÍ JEN SEM. Když jsem ji dal na celý blok, vypnul jsem s ní
            // i HandleToggles — a v tom je Tab. Kdo se přepnul do velitele, už se nedostal
            // zpátky a nefungovalo mu ani F3 a F9. Klávesy se ovládáním myši nesmí řídit.
            if (!_commander.Commanding)
            {
                HandleLook();
            }

            HandleToggles(keyboard);
            HandleHotbarKeys(keyboard);
        }
        HandleCommanderInput();

        // VELITEL NEMÁ RUCE. Není to zjednodušení, je to hook hry —
        // kdyby shora mohl kopat, jsou to dvě hry místo dvou režimů.
        if (_commander.Abilities.CanDigByHand)
        {
            HandleBlockEditing();
        }

        // Dokud chunk pod hráčem neexistuje, gravitace se neuplatní — jinak by hráč
        // propadl skrz ještě nenačtený svět dřív, než se stihne vygenerovat.
        Vector3i standing = VoxelWorld.ToChunkPosition(
            (int)MathF.Floor(_player.Position.X),
            (int)MathF.Floor(_player.Position.Y),
            (int)MathF.Floor(_player.Position.Z));

        if (!_world.HasChunk(standing) && !_player.NoClip)
        {
            _movementBlocked = true;
            return;
        }

        _movementBlocked = false;

        Vector3 forward = _camera.HorizontalForward;
        Vector3 right = _camera.Right;

        Vector3 wish = Vector3.Zero;
        if (!typingInInventorySearch)
        {
            if (keyboard.IsKeyDown(Keys.W)) { wish += forward; }
            if (keyboard.IsKeyDown(Keys.S)) { wish -= forward; }
            if (keyboard.IsKeyDown(Keys.D)) { wish += right; }
            if (keyboard.IsKeyDown(Keys.A)) { wish -= right; }
        }

        float vertical = 0f;
        if (!typingInInventorySearch)
        {
            if (keyboard.IsKeyDown(Keys.Space)) { vertical += 1f; }
            if (keyboard.IsKeyDown(Keys.LeftControl)) { vertical -= 1f; }
        }

        // POHYB SE TADY JEN ZAPÍŠE, NEPROVEDE. Vstup se musí číst každý snímek, jinak by se
        // při 144 Hz zahodily dvě třetiny stisků; fyzika ale běží pevným krokem v SimulateTick,
        // jinak by hráč na rychlejším stroji skákal jinak vysoko.
        _movementIntent = new MovementIntent(
            wish,
            Jump: !typingInInventorySearch && keyboard.IsKeyDown(Keys.Space),
            Sprint: !typingInInventorySearch && keyboard.IsKeyDown(Keys.LeftShift),
            VerticalWish: vertical,
            Crouch: !typingInInventorySearch && keyboard.IsKeyDown(Keys.C));
    }

    private bool HasActiveModScreen =>
        _mods?.Screens.HasActiveScreen == true || _mods?.Client.Ui.HasActiveScreen == true;

    private void OnModScreenChanged(ActiveModScreen? active)
    {
        if (active is not null)
        {
            _mods?.Client.Ui.CloseActiveScreen();
        }

        OnModScreenChanged(HasActiveModScreen);
    }

    private void OnModScreenV2Changed(Tesseris.Game.Modding.Client.ActiveModScreenV2? active)
    {
        if (active is not null)
        {
            _mods?.Screens.CloseActiveScreen();
        }

        OnModScreenChanged(HasActiveModScreen);
    }

    private void DispatchModUiInput(Tesseris.ModApi.ModUiInput input)
    {
        if (_mods?.Client.Ui.HasActiveScreen == true)
        {
            _mods.Client.Ui.DispatchInput(input);
        }
        else
        {
            _mods?.Screens.DispatchInput(input);
        }
    }

    private void OnModScreenChanged(bool isOpen)
    {
        if (isOpen)
        {
            if (_inventoryScreen is not null)
            {
                _inventoryScreen.Open = false;
                _inventoryScreen.Furnace = null;
                _inventoryScreen.Chest = null;
            }

            _mining?.Stop();
            CursorState = CursorState.Normal;
        }
        else if (_startupStage == StartupStage.Playing)
        {
            CursorState = CursorState.Grabbed;
            _ignoreNextMouseDelta = true;
        }

        _modScreenPointer = MouseState.Position;
        _modScreenLeftWasDown = MouseState.IsButtonDown(MouseButton.Left);
        _modScreenRightWasDown = MouseState.IsButtonDown(MouseButton.Right);
        _leftWasDown = _modScreenLeftWasDown;
        _rightWasDown = _modScreenRightWasDown;
    }

    private void DispatchModScreenPointer()
    {
        if (!HasActiveModScreen)
        {
            return;
        }

        Vector2 pointer = MouseState.Position;
        Vector2 delta = pointer - _modScreenPointer;
        if (delta != Vector2.Zero)
        {
            DispatchModUiInput(new Tesseris.ModApi.ModUiInput(
                Tesseris.ModApi.ModUiInputKind.PointerMove,
                Tesseris.ModApi.ModInputPhase.Held,
                (int)pointer.X,
                (int)pointer.Y));
            _modScreenPointer = pointer;
        }

        DispatchPointerButton(
            MouseState.IsButtonDown(MouseButton.Left),
            ref _modScreenLeftWasDown,
            Tesseris.ModApi.ModPointerButton.Primary,
            pointer);
        DispatchPointerButton(
            MouseState.IsButtonDown(MouseButton.Right),
            ref _modScreenRightWasDown,
            Tesseris.ModApi.ModPointerButton.Secondary,
            pointer);
    }

    private void DispatchPointerButton(
        bool down,
        ref bool wasDown,
        Tesseris.ModApi.ModPointerButton button,
        Vector2 pointer)
    {
        if (down == wasDown || !HasActiveModScreen)
        {
            return;
        }

        DispatchModUiInput(new Tesseris.ModApi.ModUiInput(
            Tesseris.ModApi.ModUiInputKind.PointerButton,
            down ? Tesseris.ModApi.ModInputPhase.Pressed : Tesseris.ModApi.ModInputPhase.Released,
            (int)pointer.X,
            (int)pointer.Y,
            button));
        wasDown = down;
    }

    /// <summary>
    /// Největší posun myši, který se ještě bere jako pohyb ruky, v dílcích za snímek.
    /// </summary>
    /// <remarks>
    /// I hodně rychlý pohyb dá při 1600 DPI kolem tisícovky dílků za vteřinu, tedy při
    /// stošedesáti snímcích řádově šest za snímek. Tisíc je proto jistě umělý skok:
    /// přepnutí do okna a zpět, probuzení z uspání nebo první snímek po zachycení kurzoru.
    /// Bez téhle pojistky se pohled po alt-tabu otočí o stovky stupňů.
    /// </remarks>
    private const float MaxMouseStep = 1000f;

    /// <summary>
    /// Otočí pohled podle myši.
    /// </summary>
    /// <remarks>
    /// <para><b>Posun se počítá z polohy, kterou si držíme sami</b>, ne z
    /// <c>MouseState.Delta</c>. Delta platí od posledního vstupního snímku, takže kdyby se
    /// tahle metoda zavolala v jednom snímku dvakrát — což se stane, jakmile OpenTK dohání
    /// skluz — použil by se tentýž posun podruhé a pohled by se otočil dvakrát. Rozdíl
    /// dvou poloh je proti tomu odolný: podruhé vyjde nula.</para>
    ///
    /// <para>Že vstup není zkreslený akcelerací systému, zařizuje <c>RawMouseInput</c>
    /// nastavený při zachycení kurzoru.</para>
    /// </remarks>
    private void HandleLook()
    {
        // S otevřeným inventářem myš ovládá panel, ne pohled.
        if (_inventoryScreen?.Open == true || _pauseMenuOpen)
        {
            _lastMousePosition = MouseState.Position;
            return;
        }

        Vector2 position = MouseState.Position;

        // První snímek po zachycení kurzoru nemá s čím porovnávat — poloha bývá kdekoli.
        if (_ignoreNextMouseDelta)
        {
            _ignoreNextMouseDelta = false;
            _lastMousePosition = position;
            return;
        }

        Vector2 delta = position - _lastMousePosition;
        _lastMousePosition = position;

        if (MathF.Abs(delta.X) > MaxMouseStep || MathF.Abs(delta.Y) > MaxMouseStep)
        {
            return;
        }

        if (delta == Vector2.Zero)
        {
            return;
        }

        // Osa Y je obrácená: myš dopředu znamená v souřadnicích okna nahoru, tedy menší Y,
        // a pohled se má zvednout.
        _camera.ApplyLook(delta.X * _mouseSensitivity, -delta.Y * _mouseSensitivity);
    }

    private void HandleToggles(KeyboardState keyboard)
    {
        // Hrana stisku se hlídá vlastním příznakem: chování IsKeyPressed se v neinteraktivním
        // prostředí nedalo ověřit, tohle je deterministické.
        HandleDebugMenuKeys(keyboard);

        bool f3Down = keyboard.IsKeyDown(Keys.F3);
        if (f3Down && !_f3WasDown)
        {
            _overlayVisible = !_overlayVisible;
        }

        _f3WasDown = f3Down;

        bool fDown = keyboard.IsKeyDown(Keys.F);
        if (fDown && !_fWasDown)
        {
            _player.NoClip = !_player.NoClip;
            Log.Info(_player.NoClip ? "Průchod zdmi zapnutý." : "Průchod zdmi vypnutý.");
        }

        _fWasDown = fDown;

        bool eDown = keyboard.IsKeyDown(Keys.E);
        if (eDown && !_eWasDown && _inventoryScreen is not null)
        {
            if (_inventoryScreen.Open)
            {
                CloseInventory();
            }
            else
            {
                // Klávesa E otevírá batoh, ne pec — ta se drží jen po dobu, co ji hráč
                // otevřel pravým tlačítkem.
                _inventoryScreen.Furnace = null;
                _inventoryScreen.Chest = null;
                _inventoryScreen.Open = true;

                // KURZOR SE MUSÍ PUSTIT. Se zachyceným kurzorem není čím do slotů klikat —
                // myš je celou dobu uprostřed a hýbe pohledem.
                CursorState = CursorState.Normal;
            }
        }

        _eWasDown = eDown;

        bool gDown = keyboard.IsKeyDown(Keys.G);
        if (gDown && !_gWasDown)
        {
            _creative = !_creative;
            _player.NoClip = _creative;

            // Mřížka všech předmětů je vidět jen v kreativu; v přežití je na jejím místě
            // výroba. Musí se to přepnout tady, ne až při kreslení — panel může být zrovna
            // otevřený a jinak by v něm zůstala výroba, dokud ho hráč nezavře.
            if (_inventoryScreen is not null)
            {
                _inventoryScreen.Creative = _creative;
            }

            Log.Info(_creative ? "Kreativní režim." : "Přežití.");
        }

        _gWasDown = gDown;

        bool f5Down = keyboard.IsKeyDown(Keys.F5);
        if (f5Down && !_f5WasDown && !_modCommandOpen)
        {
            _cameraView = _cameraView switch
            {
                CameraView.First => CameraView.ThirdBack,
                CameraView.ThirdBack => CameraView.ThirdFront,
                _ => CameraView.First,
            };
            Log.Info($"Pohled: {_cameraView}");
        }
        _f5WasDown = f5Down;

        // SONDA NA BLIKÁNÍ SE PŘESUNULA NA F10. F9 si vzal panel obrazu, protože se ovládá
        // často, kdežto tahle sonda je diagnostika na jednorázové použití.
        bool f10Down = keyboard.IsKeyDown(Keys.F10);
        if (f10Down && !_f10WasDown)
        {
            StartFlickerRecording();
        }

        _f10WasDown = f10Down;

        bool vDown = keyboard.IsKeyDown(Keys.V);
        if (vDown && !_vWasDown)
        {
            _chiselMode = !_chiselMode;
            Log.Info(_chiselMode ? "Režim tesání." : "Režim bloků.");
        }

        _vWasDown = vDown;

        bool rDown = keyboard.IsKeyDown(Keys.R);
        if (rDown && !_rWasDown)
        {
            _chisel.CycleMode();
            Log.Info($"Nástroj: {_chisel.Mode}.");
        }

        _rWasDown = rDown;

        bool tDown = keyboard.IsKeyDown(Keys.T);
        if (tDown && !_tWasDown)
        {
            _chisel.CycleSize();
            Log.Info($"Velikost nástroje: {_chisel.Size}.");
        }

        // RYCHLOST ČASU NA PLUS A MINUS.
        //
        // Bere se hrana stisku, ne držení: krok je násobek, takže by při držení rychlost
        // během vteřiny vyletěla o řády. Přijímají se obě klávesy — hlavní řada i numerická,
        // protože na numerické jsou plus a minus samostatné a lidem sedí líp.
        bool plusDown = keyboard.IsKeyDown(Keys.Equal) || keyboard.IsKeyDown(Keys.KeyPadAdd);
        bool minusDown = keyboard.IsKeyDown(Keys.Minus) || keyboard.IsKeyDown(Keys.KeyPadSubtract);
        bool slashDown = keyboard.IsKeyDown(Keys.Slash) || keyboard.IsKeyDown(Keys.KeyPadDivide);
        bool shiftDown = keyboard.IsKeyDown(Keys.LeftShift) || keyboard.IsKeyDown(Keys.RightShift);
        bool multiplyDown = keyboard.IsKeyDown(Keys.KeyPadMultiply)
            || (shiftDown && keyboard.IsKeyDown(Keys.D8));

        // PŘÍKAZOVÝ ŘÁDEK MÁ PŘEDNOST. Lomítko otevírá příkazovou řádku a současně zamykalo
        // slunce, takže se nedal napsat příkaz, aniž by se zastavil čas; plus a minus zase
        // při psaní měnily rychlost. Když se píše, patří klávesy textu.
        if (_modCommandOpen)
        {
            plusDown = false;
            minusDown = false;
            slashDown = false;
            multiplyDown = false;
        }

        if (slashDown && !_slashWasDown && _chunkRenderer is not null)
        {
            DayCycle day = _chunkRenderer.Day;
            day.SunFrozen = !day.SunFrozen;

            Log.Info(day.SunFrozen
                ? $"Slunce zamčeno v čase {day.TimeOfDay:F2}; biologický čas dál běží {day.Speed:F1}×."
                : "Slunce odemčeno a znovu se pohybuje.");
        }

        if (multiplyDown && !_multiplyWasDown && _chunkRenderer is not null)
        {
            DayCycle day = _chunkRenderer.Day;
            day.NatureSpeed = day.NatureSpeed < 1f
                ? 1f
                : MathF.Min(day.NatureSpeed * 2f, 64f);

            Log.Info($"Rychlost přírody: {day.NatureSpeed:F0}×; rychlost slunce se nemění.");
        }

        if (plusDown && !_plusWasDown && _chunkRenderer is not null)
        {
            DayCycle day = _chunkRenderer.Day;

            // Ze stojícího času se rozjede na jednonásobek, jinak se zdvojnásobí.
            day.Speed = day.Speed < 0.05f ? 1f : MathF.Min(day.Speed * 2f, 512f);
            day.Running = true;

            Log.Info($"Rychlost času: {day.Speed:F1}x (den za {DayCycle.DayLength / day.Speed:F0} s).");
        }

        if (minusDown && !_minusWasDown && _chunkRenderer is not null)
        {
            DayCycle day = _chunkRenderer.Day;

            day.Speed = day.Speed <= 1f ? 0f : day.Speed / 2f;
            day.Running = day.Speed > 0f;

            Log.Info(day.Running
                ? $"Rychlost času: {day.Speed:F1}x (den za {DayCycle.DayLength / day.Speed:F0} s)."
                : "Čas zastaven.");
        }

        _plusWasDown = plusDown;
        _minusWasDown = minusDown;
        _slashWasDown = slashDown;
        _multiplyWasDown = multiplyDown;

        _tWasDown = tDown;
    }

    private void HandleHotbarKeys(KeyboardState keyboard)
    {
        if (_hotbar is null)
        {
            return;
        }

        for (int slot = 0; slot < 9; slot++)
        {
            bool mainKeyboardStar = slot == 7
                && keyboard.IsKeyDown(Keys.D8)
                && (keyboard.IsKeyDown(Keys.LeftShift) || keyboard.IsKeyDown(Keys.RightShift));

            if (!keyboard.IsKeyDown(Keys.D1 + slot) || mainKeyboardStar)
            {
                continue;
            }

            // VYBÍRÁ SE Z INVENTÁŘE V OBOU REŽIMECH.
            //
            // Dřív měl kreativ vlastní paletu bloků na klávesách. Mělo to dvě vady: do devíti
            // kláves se nevešlo ani padesát bloků, natožpak nástroje a suroviny, a hlavně
            // existovaly dva zdroje toho, „co má hráč v ruce". Teď je zdroj jeden — inventář —
            // a v kreativu se z něj jen neubírá. Materiál se bere z mřížky předmětů (E).
            //
            // Paleta zůstala jen pro materiál dláta, které je samostatná mechanika.
            _inventory?.Select(slot);
            _hotbar.Select(slot);
        }
    }

    /// <summary>
    /// Kopání do bloku, dokud se nerozpadne.
    /// </summary>
    /// <remarks>
    /// <para><b>Co z bloku vypadne, se nedává do inventáře, ale na zem.</b> Hráč pak vidí,
    /// co vytěžil, může to nechat ležet a hlavně je z toho zpětná vazba. Rovnou do
    /// inventáře je tichý přesun, o kterém se člověk doví jen tím, že se mu někde změnilo
    /// číslo.</para>
    ///
    /// <para>V kreativu blok mizí hned a nic nezanechá: stavění je tam celý smysl a hromada
    /// věcí na zemi by jen překážela.</para>
    /// </remarks>
    /// <summary>Co má hráč v ruce, do přehledu dole. Dokud není inventářový panel.</summary>
    private string HandLine()
    {
        if (_inventory is null || _items is null)
        {
            return DebugOverlay.HotbarLine(_hotbar!.SelectedIndex + 1, _hotbar.SelectedName);
        }

        ItemStack stack = _inventory.SelectedStack;

        if (stack.IsEmpty)
        {
            string hand = GameLocalization.IsEnglish(UiLanguage) ? "hand" : "ruka";
            string creative = GameLocalization.IsEnglish(UiLanguage) ? "creative" : "kreativ";
            return DebugOverlay.HotbarLine(_inventory.Selected + 1, _creative ? $"{hand} ({creative})" : hand);
        }

        ItemDefinition definition = _items.Definition(stack.Item);

        string name = GameLocalization.ItemName(definition, UiLanguage);
        string label = _creative
            ? $"{name} ({(GameLocalization.IsEnglish(UiLanguage) ? "creative" : "kreativ")})"
            : name;

        if (stack.Count > 1)
        {
            label += $" x{stack.Count}";
        }

        // Opotřebení se ukazuje jen u nástroje, který ho má. U bloku by bylo matoucí.
        if (definition.Durability > 0)
        {
            label += $"  [{definition.Durability - stack.Damage}/{definition.Durability}]";
        }

        int lying = _drops?.Count ?? 0;

        string line = DebugOverlay.HotbarLine(_inventory.Selected + 1, label)
            + (lying > 0 ? $"   na zemi: {lying}" : string.Empty);

        if (definition.Id == "tesseris:overhead_wire_coil")
        {
            line += _pendingWireEndpoint is null
                ? "   PPM na první svorku"
                : "   PPM na druhou svorku";
        }
        else if (definition.Id.StartsWith("tesseris:surface_cable_", StringComparison.Ordinal))
        {
            line += "   PPM na stěnu; stejná barva se spojí i přes roh";
        }
        else if (definition.Id == "tesseris:wire_cutters")
        {
            line += "   klik na kabel nebo svorku = odstranit";
        }

        if (_pendingWireEndpoint is { } endpoint)
        {
            line += $"   první svorka: {endpoint.X}/{endpoint.Y}/{endpoint.Z}";
        }

        return line;
    }

    /// <summary>
    /// Drží hráč kbelík?
    /// </summary>
    /// <remarks>
    /// <para><b>V přežití rozhoduje ruka, v kreativu paleta.</b> Paleta má kbelík na slotu
    /// nula a ten je po startu vybraný — takže dokud se ptalo jen jí, byl kbelík v ruce
    /// pořád a těžba se nespustila ani jednou. Přesně tak se to i projevilo: mačkalo se,
    /// drželo a nic.</para>
    /// </remarks>
    /// <summary>
    /// Jaký objem těžený blok opravdu zabírá, v souřadnicích uvnitř bloku.
    /// </summary>
    /// <remarks>
    /// Kmen je sloupek o poloviční šířce, takže praskliny přes celý blok se u něj kreslily
    /// vedle dřeva, na hranici bloku. Ostatní tvary blok vyplňují, u nich to vyjde na celek.
    /// </remarks>
    private Aabb? BreakingBounds(Vector3i block)
    {
        var whole = new Aabb(Vector3.Zero, Vector3.One);

        if (_world is null)
        {
            return whole;
        }

        BlockShape shape = _world.Registry.Definition(_world.GetBlock(block.X, block.Y, block.Z)).Shape;

        return shape switch
        {
            // POROST PRASKLINY NEDOSTANE. Tráva, kytka i chomáč listí jsou zkřížené plochy
            // bez objemu — krychle prasklin kolem nich visí ve vzduchu a s tím, co je vidět,
            // nemá nic společného. Navíc padnou během jednoho dvou snímků, takže by stejně
            // jen problikly.
            BlockShape.Cross or BlockShape.Foliage or BlockShape.GroundClutter => null,

            // Kmen je sloupek o poloviční šířce, viz PieceMask.Post.
            BlockShape.Post => PieceMask.Post,

            _ => whole,
        };
    }

    /// <summary>
    /// Přesouvání předmětů v otevřeném panelu.
    /// </summary>
    /// <remarks>
    /// <para>Levé tlačítko bere a pokládá celou hromádku, pravé půlku a po kusu — tak to
    /// dělá každá bloková hra a hráč to nemusí hledat.</para>
    ///
    /// <para>Nesené se drží v <c>Inventory.Held</c>, ne ve slotu: kdyby leželo ve slotu,
    /// dalo by se zavřením panelu zdvojit.</para>
    /// </remarks>
    private void HandleInventoryMouse(bool leftDown, bool rightDown)
    {
        if (_inventory is null || _inventoryScreen is null || _items is null)
        {
            return;
        }

        bool leftPressed = leftDown && !_leftWasDown;
        bool rightPressed = rightDown && !_rightWasDown;

        if (!leftPressed && !rightPressed)
        {
            return;
        }

        Vector2 mouse = MouseState.Position;

        if (leftPressed)
        {
            if (_inventoryScreen.SearchBoxAt(mouse, ClientSize.X, ClientSize.Y))
            {
                _inventoryScreen.SearchFocused = true;
                return;
            }

            _inventoryScreen.SearchFocused = false;
        }

        // KNIHA RECEPTŮ. Ptá se první, protože leží nad sloupcem výroby.
        if (_inventoryScreen.BookButtonAt(mouse, ClientSize.X, ClientSize.Y) && leftPressed)
        {
            _inventoryScreen.BookOpen = !_inventoryScreen.BookOpen;
            _inventoryScreen.BookScroll = 0;
            return;
        }

        // VÝROBA MÁ PŘEDNOST PŘED SLOTY. Mřížka receptů leží vedle batohu, takže se
        // nepřekrývají — ale kdyby se jednou překryly, je klik na recept to, co hráč chtěl.
        int recipe = _inventoryScreen.RecipeAt(mouse, ClientSize.X, ClientSize.Y);

        if (recipe >= 0 && leftPressed && _recipes is not null)
        {
            // Mřížka ukazuje jen dostupné recepty, takže index je do TOHO seznamu.
            List<int> available = InventoryScreen.Available(_inventory, _recipes);

            if (recipe < available.Count)
            {
                RecipeBook.Make(
                    _inventory, _recipes.Crafting[available[recipe]], _drops,
                    _player.EyePosition + (_camera.Forward * 0.6f));
            }

            return;
        }

        // VÝSTROJ. Sloty se chovají jako každé jiné, jen leží ve vlastním sloupci.
        int armour = _inventoryScreen.ArmourAt(mouse, ClientSize.X, ClientSize.Y);

        if (armour >= 0)
        {
            // Do slotu se vejde jen kus, který tam patří. Vytáhnout se dá vždycky.
            if (_inventory.FitsArmourSlot(_inventory.Held, armour))
            {
                int worn = Inventory.FirstArmourSlot + armour;

                (_inventory.Held, _inventory[worn]) = (_inventory[worn], _inventory.Held);
            }

            return;
        }

        // VYPÍNAČ PECE. Musí se ptát dřív než sloty, protože leží vedle horního slotu.
        if (_inventoryScreen.FurnaceButtonAt(mouse, ClientSize.X, ClientSize.Y) && leftPressed)
        {
            TogglePower();
            return;
        }

        int chestSlot = _inventoryScreen.ChestSlotAt(mouse, ClientSize.X, ClientSize.Y);

        if (chestSlot >= 0)
        {
            SwapChestSlot(chestSlot, leftPressed);
            return;
        }

        int furnaceSlot = _inventoryScreen.FurnaceSlotAt(mouse, ClientSize.X, ClientSize.Y);

        if (furnaceSlot >= 0)
        {
            SwapFurnaceSlot(furnaceSlot);
            return;
        }

        // KREATIVNÍ MŘÍŽKA PŘEDMĚTŮ. Klik vezme plnou hromádku do ruky, pravé tlačítko
        // jeden kus — tak se dá vzít i nástroj bez toho, aby se v ruce objevilo šedesát čtyři
        // krumpáčů.
        int creativeItem = _inventoryScreen.CreativeAt(mouse, ClientSize.X, ClientSize.Y);

        if (creativeItem != ItemRegistry.Nothing)
        {
            int count = leftPressed ? _items.Definition(creativeItem).MaxStack : 1;

            _inventory.Held = new ItemStack(creativeItem, count, 0);
            return;
        }

        int slot = _inventoryScreen.SlotAt(mouse, ClientSize.X, ClientSize.Y);

        if (slot < 0)
        {
            // Klik mimo sloty: nesené se vyhodí do světa, ne aby se ztratilo.
            DropHeld();
            return;
        }

        ItemStack inSlot = _inventory[slot];
        ItemStack held = _inventory.Held;

        if (leftPressed)
        {
            // Slití dvou stejných hromádek, jinak prohození.
            if (!held.IsEmpty && !inSlot.IsEmpty && inSlot.Matches(held))
            {
                int max = _items.Definition(inSlot.Item).MaxStack;
                int moved = Math.Min(max - inSlot.Count, held.Count);

                _inventory[slot] = inSlot.WithCount(inSlot.Count + moved);
                _inventory.Held = held.WithCount(held.Count - moved);
                return;
            }

            _inventory[slot] = held;
            _inventory.Held = inSlot;
            return;
        }

        // Pravé tlačítko: z plné ruky po jednom, z prázdné ruky půlka slotu.
        if (!held.IsEmpty)
        {
            if (inSlot.IsEmpty)
            {
                _inventory[slot] = held.WithCount(1);
                _inventory.Held = held.WithCount(held.Count - 1);
            }
            else if (inSlot.Matches(held) && inSlot.Count < _items.Definition(inSlot.Item).MaxStack)
            {
                _inventory[slot] = inSlot.WithCount(inSlot.Count + 1);
                _inventory.Held = held.WithCount(held.Count - 1);
            }

            return;
        }

        if (inSlot.IsEmpty)
        {
            return;
        }

        int half = (inSlot.Count + 1) / 2;

        _inventory.Held = inSlot.WithCount(half);
        _inventory[slot] = inSlot.WithCount(inSlot.Count - half);
    }

    /// <summary>
    /// Prohodí nesené s obsahem slotu pece.
    /// </summary>
    /// <remarks>
    /// <para><b>Z výstupu se jen bere.</b> Dát do něj něco by znamenalo, že tam hráč
    /// zamkne surovinu, kterou pec nikdy nezpracuje.</para>
    ///
    /// <para>Do paliva jde jen to, co hoří, a do vstupu jen to, co se dá tavit. Bez toho
    /// by šlo dát krumpáč pod pec a čekat, co se stane.</para>
    /// </remarks>
    /// <summary>
    /// Klik do slotu pece: 0 palivo, 1–2 suroviny, 3–5 výstup.
    /// </summary>
    /// <remarks>
    /// <para><b>Do paliva i do surovin se dá dát cokoli.</b> Původně slot odmítal všechno,
    /// co neumí hořet nebo se tavit — jenže odmítal to <b>beze slova</b>: hráč klikal a
    /// předmět prostě nešel dovnitř, což se čte jako rozbité UI, ne jako pravidlo. Přesně
    /// tak to bylo nahlášeno („do inputu mi to prostě nejde dát"). Teď se předmět položit
    /// dá a pec s ním jen nic neudělá, což je vidět na tom, že se plamen ani postup nehnou.</para>
    ///
    /// <para><b>Z výstupu se jen bere.</b> Není to skladiště — kdyby se do něj dalo pokládat,
    /// zabral by hráč peci místo, kam má odkládat hotové.</para>
    /// </remarks>
    private void SwapFurnaceSlot(int index)
    {
        if (_inventoryScreen?.Furnace is not { } furnace || _inventory is null || _furnaces is null)
        {
            return;
        }

        ItemStack held = _inventory.Held;

        if (index == 0)
        {
            (_inventory.Held, furnace.Fuel) = (furnace.Fuel, held);
            return;
        }

        if (index < 1 + Furnace.InputSlots)
        {
            int slot = index - 1;

            (_inventory.Held, furnace.Input[slot]) = (furnace.Input[slot], held);
            return;
        }

        int output = index - 1 - Furnace.InputSlots;

        if (held.IsEmpty)
        {
            _inventory.Held = furnace.Output[output];
            furnace.Output[output] = ItemStack.Empty;
        }
    }

    /// <summary>
    /// Zapne nebo vypne pec.
    /// </summary>
    /// <remarks>
    /// Zapnout jde jen s palivem. Bez téhle podmínky by pec šlo „zapnout" a ona by se
    /// v témž snímku sama vypnula, protože nemá čím hořet — hráč by z toho viděl jen to,
    /// že tlačítko nefunguje.
    /// </remarks>
    private void TogglePower()
    {
        if (_inventoryScreen?.Furnace is not { } furnace)
        {
            return;
        }

        if (furnace.Running)
        {
            furnace.Running = false;
            return;
        }

        if (furnace.Burning || (_furnaces?.IsFuel(furnace.Fuel) ?? false))
        {
            furnace.Running = true;
        }
    }

    /// <summary>Poloviční velikost ručního modelu ve světě.</summary>
    /// <remarks>
    /// Větší než u vytažené ikony: sekera má být v ruce znát jako nářadí, ne jako placka.
    /// Model se podle tohohle čísla zmenší celý, ať už je v jednotkách jakkoli velký.
    /// </remarks>
    private const float ItemModelHalf = 0.22f;

    /// <summary>
    /// Přiřadí načtené ruční modely předmětům, které si o ně řekly.
    /// </summary>
    /// <remarks>
    /// Model se páruje jménem z <c>ItemDefinition.Model</c>. Nespárovaný model není chyba —
    /// dá se ho takhle nechat ve složce ležet, než se pro něj najde předmět.
    /// </remarks>
    private void BindItemModels(List<ItemModelFile> models, List<string> layers)
    {
        if (_items is null || _chunkRenderer is null || models.Count == 0)
        {
            return;
        }

        Dictionary<string, ItemModelFile> byName = [];

        foreach (ItemModelFile model in models)
        {
            model.Resolve(name => layers.IndexOf(name));
            byName[model.Name] = model;
        }

        Dictionary<int, ItemShape> bound = [];

        for (int item = 0; item < _items.Count; item++)
        {
            string name = _items.Definition(item).Model;

            if (!string.IsNullOrEmpty(name) && byName.TryGetValue(name, out ItemModelFile? model))
            {
                bound[item] = model.Shape;
            }
        }

        _chunkRenderer.ItemModels = bound;

        Log.Info($"Načteno {models.Count} ručních modelů, přiřazeno {bound.Count} předmětům.");
    }

    /// <summary>
    /// Řekne rendereru, co má hráč v ruce, a posune rozmach.
    /// </summary>
    /// <remarks>
    /// <para><b>Ruka je vidět vždycky, i prázdná.</b> Bez ní se hráč dívá do světa jako
    /// kamera na stativu — nic mu nedrží měřítko a údery nemají co ukázat.</para>
    ///
    /// <para>S otevřeným panelem se nekreslí: hráč v tu chvíli přerovnává inventář a ruka
    /// by mu ležela přes sloty.</para>
    /// </remarks>
    /// <summary>
    /// Posune rozmach ruky. Herní časování, proto v pevném tiku.
    /// </summary>
    /// <remarks>
    /// Zvlášť od <see cref="UpdateHeld"/> schválně. Tempo úderu je herní stav a musí být
    /// deterministické; kde ruka na obrazovce visí, je vizuál a patří po snímcích.
    /// </remarks>
    private void AdvanceSwing(float seconds)
    {
        // Rozmach doběhne vždycky celý. Půl vteřiny na úder je akorát: rychlejší se ztratí,
        // pomalejší se rozchází s tím, jak často se dá kopat.
        if (_swing <= 0f)
        {
            return;
        }

        // TEMPO PODLE LUANTI: `m_digging_anim += dtime * 3.5f`
        // (references/luanti/src/client/camera.cpp:185), tedy cyklus za 0,29 s.
        // Naše 2,2 znamenalo 0,45 s, což bylo znatelně loudavější.
        _swing = MathF.Min(1f, _swing + (seconds * 3.5f));

        if (_swing >= 1f)
        {
            _swing = 0f;
        }
    }

    /// <summary>
    /// Postaví drženou ruku nebo předmět před kameru.
    /// </summary>
    /// <remarks>
    /// <b>Musí se volat až po nastavení kamery v tomtéž snímku.</b> Ukládá si její polohu
    /// i všechny tři osy, takže z tiku by dostala kameru z minulého snímku — a při letu by
    /// se propadla za near plane a zmizela.
    /// </remarks>
    private void UpdateHeld()
    {
        if (_chunkRenderer is null || _items is null)
        {
            return;
        }

        // RUKA JEN Z PRVNÍ OSOBY. Ve třetí by visela před kamerou přes celou postavu —
        // předmět v ruce má v tom pohledu držet model, ne overlay.
        // VELITEL NEMÁ RUCE. Je to hook hry, ne detail:
        // shora se plánuje, rukama se pracuje dole. Ruka visící nad pohledem shora ten rozdíl
        // maže — a k tomu se otáčela s myší, kterou velitel ukazuje do mapy.
        if (_inventoryScreen?.Open == true
            || _cameraView != CameraView.First
            || _commander.Commanding
            || _commander.InTransition)
        {
            _chunkRenderer.Held = null;
            return;
        }

        ItemStack stack = _inventory?.SelectedStack ?? ItemStack.Empty;

        bool empty = stack.IsEmpty;
        int layer = empty ? _playerSkinLayer : _items.IconLayer(stack.Item);

        if (layer < 0)
        {
            _chunkRenderer.Held = null;
            return;
        }

        byte heldPieces = PieceMask.Full;
        ChunkRenderer.HeldKind kind;
        if (empty)
        {
            kind = ChunkRenderer.HeldKind.Hand;
        }
        else if (_items.IsFlat(stack.Item))
        {
            kind = ChunkRenderer.HeldKind.Flat;
        }
        else if (_items.TryGetBlockIcon(stack.Item, out BlockItemIcon blockIcon)
            && blockIcon.Pieces != PieceMask.Full)
        {
            kind = ChunkRenderer.HeldKind.ShapedBlock;
            heldPieces = blockIcon.Pieces;
        }
        else
        {
            kind = ChunkRenderer.HeldKind.Block;
        }

        _chunkRenderer.Held = new ChunkRenderer.HeldItem(
            empty ? ItemRegistry.Nothing : stack.Item,
            layer,
            kind,
            _camera.Position,
            _camera.Right,
            _camera.Up,
            _camera.Forward,
            _swing,
            heldPieces);
    }

    /// <summary>Spustí rozmach, pokud zrovna žádný neběží.</summary>
    private void StartSwing()
    {
        if (_swing <= 0f)
        {
            _swing = 0.001f;
        }
    }

    /// <summary>
    /// Životy a dech nad pásem.
    /// </summary>
    /// <remarks>
    /// <para><b>Řádky, ne čísla.</b> Deset srdcí se dá přečíst jedním pohledem i periferně,
    /// kdežto „14/20" se musí přečíst a přepočítat — a při pádu na to není čas.</para>
    ///
    /// <para>Bubliny se ukazují jen pod vodou. Nad hladinou by to byl jen další řádek, který
    /// vždycky svítí plný, a hráč by ho přestal vnímat právě do chvíle, kdy na něm záleží.</para>
    ///
    /// <para>V kreativu se neukazuje nic: nemá se co ubírat.</para>
    /// </remarks>
    private void DrawVitals()
    {
        if (_sprites is null || _creative || _inventoryScreen?.Open == true)
        {
            return;
        }

        const int Hearts = 10;

        float size = InventoryScreen.SlotSize(ClientSize.Y);
        float pip = size * 0.32f;
        float step = pip * 1.15f;

        float left = ((ClientSize.X - (Inventory.HotbarSlots * size)) * 0.5f) + (size * 0.06f);
        float bottom = ClientSize.Y - size - (size * 0.25f) - (pip * 1.5f);

        float perHeart = Vitals.MaxHealth / Hearts;

        for (int i = 0; i < Hearts; i++)
        {
            // Půlka srdce se pozná zmenšením, ne vlastní kresbou: pro deset pípek je to
            // čitelné a nepotřebuje to druhou ikonu.
            float filled = Math.Clamp((_vitals.Health - (i * perHeart)) / perHeart, 0f, 1f);

            _sprites.DrawRect(left + (i * step), bottom, pip, pip, new Vector4(0.12f, 0.05f, 0.06f, 0.85f));

            if (filled > 0f)
            {
                float inner = pip * filled;

                _sprites.DrawRect(
                    left + (i * step) + ((pip - inner) * 0.5f), bottom + ((pip - inner) * 0.5f),
                    inner, inner, new Vector4(0.86f, 0.18f, 0.22f, 1f));
            }
        }

        if (!_vitals.Submerged)
        {
            return;
        }

        float breath = _vitals.Breath / Vitals.MaxBreath;
        float bubbleRow = bottom - (pip * 1.35f);

        for (int i = 0; i < Hearts; i++)
        {
            if ((i + 1f) / Hearts > breath)
            {
                continue;
            }

            _sprites.DrawRect(
                left + (i * step), bubbleRow, pip, pip, new Vector4(0.35f, 0.68f, 0.95f, 0.95f));
        }
    }

    /// <summary>
    /// Vrátí mrtvého hráče na začátek.
    /// </summary>
    /// <remarks>
    /// <para><b>Inventář se vysype na místě smrti.</b> Ztratit ho beze stopy by znamenalo,
    /// že jediná chyba smaže hodinu práce; takhle se dá pro věci dojít, což je i důvod se
    /// tam vracet.</para>
    ///
    /// <para>Výstroj se vysype taky — jinak by se smrt vyplácela hráči v plné zbroji.</para>
    /// </remarks>
    private void Respawn()
    {
        if (_inventory is not null && _drops is not null)
        {
            for (int slot = 0; slot < Inventory.AllSlots; slot++)
            {
                ItemStack stack = _inventory[slot];

                if (!stack.IsEmpty)
                {
                    _drops.SpawnFromBlock(stack, (Vector3i)_player.Position, (uint)slot);
                    _inventory[slot] = ItemStack.Empty;
                }
            }

            _inventory.Held = ItemStack.Empty;
            _drops.Merge();
        }

        _player.Teleport(_spawn);
        _cameraStepOffset = 0f;
        _vitals.Reset();

        Log.Info("Hráč zemřel a vrátil se na začátek.");
    }

    /// <summary>Vyhodí nesenou hromádku před hráče.</summary>
    private void DropHeld()
    {
        if (_inventory is null || _drops is null || _inventory.Held.IsEmpty)
        {
            return;
        }

        _drops.Spawn(
            _inventory.Held,
            _player.EyePosition + (_camera.Forward * 0.6f),
            _camera.Forward * 5f);

        _inventory.Held = ItemStack.Empty;
    }

    private bool HoldingBucket()
    {
        if (_inventory is null || _items is null)
        {
            return false;
        }

        ItemStack held = _inventory.SelectedStack;

        return !held.IsEmpty && _items.Definition(held.Item).Kind == ItemKind.Bucket;
    }

    private void SwapChestSlot(int index, bool leftPressed)
    {
        if (_inventoryScreen?.Chest is not { } chest || _inventory is null || _items is null) return;

        ItemStack inSlot = chest[index];
        ItemStack held = _inventory.Held;
        if (leftPressed)
        {
            if (!held.IsEmpty && !inSlot.IsEmpty && inSlot.Matches(held))
            {
                int max = _items.Definition(inSlot.Item).MaxStack;
                int moved = Math.Min(max - inSlot.Count, held.Count);
                chest[index] = inSlot.WithCount(inSlot.Count + moved);
                _inventory.Held = held.WithCount(held.Count - moved);
            }
            else
            {
                chest[index] = held;
                _inventory.Held = inSlot;
            }
            return;
        }

        if (!held.IsEmpty)
        {
            if (inSlot.IsEmpty)
            {
                chest[index] = held.WithCount(1);
                _inventory.Held = held.WithCount(held.Count - 1);
            }
            else if (inSlot.Matches(held) && inSlot.Count < _items.Definition(inSlot.Item).MaxStack)
            {
                chest[index] = inSlot.WithCount(inSlot.Count + 1);
                _inventory.Held = held.WithCount(held.Count - 1);
            }
            return;
        }

        if (!inSlot.IsEmpty)
        {
            int half = (inSlot.Count + 1) / 2;
            _inventory.Held = inSlot.WithCount(half);
            chest[index] = inSlot.WithCount(inSlot.Count - half);
        }
    }

    private void DrawModScreen()
    {
        if (_sprites is null || _text is null || _mods is null || !HasActiveModScreen)
        {
            return;
        }

        var canvas = new GameModUiCanvas(ClientSize.X, ClientSize.Y);
        if (_mods.Client.Ui.HasActiveScreen)
        {
            _mods.Client.Ui.DrawActiveScreen(canvas);
        }
        else
        {
            _mods.Screens.Draw(canvas);
        }
        canvas.Render(_sprites, _text, _stats);
    }

    private void DrawModHud()
    {
        if (_sprites is null || _text is null || _mods is null)
        {
            return;
        }

        var canvas = new GameModUiCanvas(ClientSize.X, ClientSize.Y);
        _mods.Ui.Draw(canvas);
        _mods.Client.Ui.DrawAllHud(canvas);
        canvas.Render(_sprites, _text, _stats);
    }

    private void DrawModCommandPrompt()
    {
        if (_sprites is null || _text is null)
        {
            return;
        }

        const float Margin = 12f;
        const float PromptHeight = 42f;
        int shownMessages = Math.Min(5, _modCommandMessages.Count);
        float messageHeight = shownMessages * (TextRenderer.LineHeight + 4f);
        float promptY = ClientSize.Y - PromptHeight - Margin;

        _sprites.Begin(ClientSize.X, ClientSize.Y);
        if (shownMessages > 0)
        {
            _sprites.DrawRect(
                Margin,
                promptY - messageHeight - 8f,
                MathF.Min(ClientSize.X - (Margin * 2f), 760f),
                messageHeight + 4f,
                new Vector4(0.02f, 0.025f, 0.03f, 0.82f));
        }
        if (_modCommandOpen)
        {
            _sprites.DrawRect(
                Margin,
                promptY,
                ClientSize.X - (Margin * 2f),
                PromptHeight,
                new Vector4(0.025f, 0.03f, 0.035f, 0.96f));
            _sprites.DrawFrame(
                Margin,
                promptY,
                ClientSize.X - (Margin * 2f),
                PromptHeight,
                2f,
                new Vector4(0.25f, 0.72f, 0.43f, 1f));
        }
        _sprites.End();

        _text.Begin(ClientSize.X, ClientSize.Y);
        int first = _modCommandMessages.Count - shownMessages;
        for (int index = 0; index < shownMessages; index++)
        {
            _text.DrawText(
                ShortUiLine(_modCommandMessages[first + index], 100),
                (int)Margin + 8,
                (int)(promptY - messageHeight + (index * (TextRenderer.LineHeight + 4f))),
                1);
        }
        if (_modCommandOpen)
        {
            _text.DrawText(
                ShortUiLine(_modCommandText + "_", 120),
                (int)Margin + 10,
                (int)promptY + 11,
                1);
        }
        _text.End(_stats);
    }

    private string? SurvivalGoalText()
    {
        if (_inventory is null || _items is null)
        {
            return null;
        }

        bool english = GameLocalization.IsEnglish(UiLanguage);
        int Count(string id) => _inventory.CountOf(_items.IndexOf(id));
        bool Has(string id) => Count(id) > 0;

        bool hasDiamondPickaxe = Has("tesseris:diamond_pickaxe");
        bool hasIronPickaxe = hasDiamondPickaxe || Has("tesseris:iron_pickaxe");
        bool hasStonePickaxe = hasIronPickaxe || Has("tesseris:stone_pickaxe");
        bool hasFlintPickaxe = hasStonePickaxe || Has("tesseris:flint_pickaxe");

        if (!hasFlintPickaxe)
        {
            int flint = Count("tesseris:flint");
            int sticks = Count("tesseris:stick");
            return flint >= 3 && sticks >= 2
                ? (english ? "GOAL: craft a flint pickaxe" : "CIL: vyrob pazourkovy krumpac")
                : (english
                    ? $"GOAL: gather flint {flint}/3 and sticks {sticks}/2"
                    : $"CIL: sesbirej pazourek {flint}/3 a klacky {sticks}/2");
        }

        if (!hasStonePickaxe)
        {
            int cobble = Count("tesseris:cobblestone");
            return cobble >= 3
                ? (english ? "GOAL: craft a stone pickaxe" : "CIL: vyrob kamenny krumpac")
                : (english
                    ? $"GOAL: mine fractured stone {cobble}/3"
                    : $"CIL: vytez lamany kamen {cobble}/3");
        }

        if (!hasIronPickaxe)
        {
            int ingots = Count("tesseris:iron_ingot");
            if (ingots >= 3)
            {
                return english ? "GOAL: craft an iron pickaxe" : "CIL: vyrob zelezny krumpac";
            }

            if (Count("tesseris:raw_iron") > 0)
            {
                return english
                    ? $"GOAL: smelt iron in a stone furnace ({ingots}/3 ingots)"
                    : $"CIL: vytav zelezo v kamenne peci ({ingots}/3 ingotu)";
            }

            return english
                ? "GOAL: mine iron ore with the stone pickaxe"
                : "CIL: vytez zeleznou rudu kamennym krumpacem";
        }

        if (Count("tesseris:diamond") == 0 && !hasDiamondPickaxe)
        {
            return english
                ? "GOAL: find diamond ore and mine it with the iron pickaxe"
                : "CIL: najdi diamantovou rudu a vytez ji zeleznym krumpacem";
        }

        return english
            ? "PROGRESSION COMPLETE: explore, build and improve your gear"
            : "POSTUP DOKONCEN: objevuj, stav a vylepsuj vybavu";
    }

    /// <summary>Popisek cíle spočítaný v dávce spritů a dokreslený v textové.</summary>
    private AimInfo? _aimInfo;

    /// <summary>Co má stát v popisku „na co se dívám“.</summary>
    /// <param name="Fill">Podíl pruhu 0 až 1; záporné číslo znamená bez pruhu.</param>
    private readonly record struct AimInfo(string Title, string Detail, float Fill, bool IsMob);

    /// <summary>Dohled popisku. Delší než dosah ruky — pojmenovat jde i to, na co se nedosáhne.</summary>
    private const float AimInfoDistance = 24f;

    /// <summary>
    /// Zjistí, na co hráč míří. Zvíře má přednost jen tehdy, když je blíž než blok — jinak by
    /// se jmenovalo skrz zeď.
    /// </summary>
    private AimInfo? AimTarget()
    {
        if (_world is null || _player is null) return null;

        Vector3 eye = _player.EyePosition;
        Vector3 forward = _camera.Forward;
        bool english = GameLocalization.IsEnglish(UiLanguage);

        float blockDistance = AimInfoDistance;
        bool hasBlock = MicroRaycast.Cast(
            _world, eye, forward, AimInfoDistance, out MicroHit hit, hitLiquid: HoldingBucket());
        if (hasBlock)
        {
            blockDistance = (new Vector3(hit.Block.X + .5f, hit.Block.Y + .5f, hit.Block.Z + .5f) - eye)
                .Length;
        }

        if (_animals is not null
            && _animals.TryLookAt(eye, forward, blockDistance, out AnimalEntity? mob, out _)
            && mob is not null)
        {
            MobDefinition definition = MobDefinitions.For(mob);
            float maximum = Math.Max(1f, definition.MaximumHealth);
            return new AimInfo(
                AimName(definition.Id),
                $"HP {(int)MathF.Ceiling(Math.Max(0f, mob.Health))} / {(int)MathF.Ceiling(maximum)}",
                Math.Clamp(mob.Health / maximum, 0f, 1f),
                IsMob: true);
        }

        if (!hasBlock) return null;
        ushort block = _world.GetBlock(hit.Block.X, hit.Block.Y, hit.Block.Z);
        if (block == BlockRegistry.Air) return null;

        BlockDefinition blockDefinition = _world.Registry.Definition(block);
        bool harvestable = _mining is not null && _inventory is not null
            && _mining.CanHarvest(block, _inventory.SelectedStack);

        // Procenta jen pro blok, do kterého se opravdu kope. Jinak by ukazovala postup
        // ze sousedního bloku, na který hráč mezitím přestal mířit.
        float progress = harvestable && _mining is not null && _mining.Target == hit.Block
            ? Math.Clamp(_mining.Progress, 0f, 1f)
            : 0f;

        return new AimInfo(
            AimName(blockDefinition.Id),
            harvestable
                ? $"{(int)MathF.Round(progress * 100f)} %"
                : (english ? "CANNOT MINE" : "NELZE TEZIT"),
            harvestable ? progress : -1f,
            IsMob: false);
    }

    /// <summary>
    /// Z namespacovaného ID udělá čitelný název: <c>animalia:grizzly_bear</c> → <c>GRIZZLY BEAR</c>.
    /// Písmo umí jen ASCII velká písmena, takže se nic nepřekládá.
    /// </summary>
    private static string AimName(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return "?";
        int colon = id.LastIndexOf(':');
        string name = colon >= 0 && colon + 1 < id.Length ? id[(colon + 1)..] : id;
        return name.Replace('_', ' ').ToUpperInvariant();
    }

    /// <summary>Obdélník popisku uprostřed nahoře. Počítá se stejně pro rám i pro text.</summary>
    private (float X, float Y, float Width, float Height) AimPanelRect(AimInfo info)
    {
        const int TitleScale = 2;
        const int DetailScale = 1;
        float titleWidth = info.Title.Length * TextRenderer.AdvanceX * TitleScale;
        float detailWidth = info.Detail.Length * TextRenderer.AdvanceX * DetailScale;
        float width = Math.Max(140f, Math.Max(titleWidth, detailWidth) + 32f);
        float height = (TextRenderer.LineHeight * TitleScale)
            + (TextRenderer.LineHeight * DetailScale) + 26f;
        // Úplně nahoře. Řádek s cílem a s požadovaným nástrojem se pod něj odsune,
        // viz volání AimPanelBottom() v DrawOverlay.
        return ((ClientSize.X - width) * 0.5f, 6f, width, height);
    }

    /// <summary>Kde popisek končí, aby se pod něj vešly ostatní vycentrované řádky.</summary>
    private int AimPanelBottom() =>
        _aimInfo is { } info ? (int)(AimPanelRect(info).Y + AimPanelRect(info).Height) + 6 : 10;

    private void DrawAimPanelFrame(AimInfo info)
    {
        if (_sprites is null) return;
        (float x, float y, float width, float height) = AimPanelRect(info);

        _sprites.DrawRect(x, y, width, height, new Vector4(0.04f, 0.06f, 0.05f, 0.72f));
        _sprites.DrawFrame(x, y, width, height, 2f, new Vector4(0.33f, 0.45f, 0.36f, 0.9f));

        if (info.Fill < 0f) return;

        // Pruh pod textem: u zvířete zdraví, u bloku postup těžby.
        float barX = x + 12f;
        float barWidth = width - 24f;
        float barY = y + height - 12f;
        _sprites.DrawRect(barX, barY, barWidth, 5f, new Vector4(0.1f, 0.12f, 0.11f, 0.85f));
        _sprites.DrawRect(
            barX, barY, barWidth * info.Fill, 5f,
            info.IsMob
                ? new Vector4(0.78f, 0.24f, 0.24f, 0.95f)
                : new Vector4(0.42f, 0.76f, 0.45f, 0.95f));
    }

    private void DrawAimPanelText(AimInfo info)
    {
        if (_text is null) return;
        (float x, float y, float width, _) = AimPanelRect(info);

        int titleX = (int)(x + ((width - (info.Title.Length * TextRenderer.AdvanceX * 2)) * 0.5f));
        _text.DrawText(info.Title, titleX, (int)y + 8, 2);

        int detailX = (int)(x + ((width - (info.Detail.Length * TextRenderer.AdvanceX)) * 0.5f));
        _text.DrawText(info.Detail, detailX, (int)y + 10 + (TextRenderer.LineHeight * 2), 1);
    }

    private string? HarvestRequirementText()
    {
        if (_world is null || _mining is null || _inventory is null
            || !MicroRaycast.Cast(
                _world, _player.EyePosition, _camera.Forward, ReachDistance, out MicroHit hit,
                hitLiquid: false))
        {
            return null;
        }

        ushort block = _world.GetBlock(hit.Block.X, hit.Block.Y, hit.Block.Z);
        if (block == BlockRegistry.Air || _mining.CanHarvest(block, _inventory.SelectedStack))
        {
            return null;
        }

        BlockDefinition definition = _world.Registry.Definition(block);
        ToolTier tier = Mining.RequiredTier(definition);
        ToolKind tool = Mining.PreferredTool(definition.Material);
        bool english = GameLocalization.IsEnglish(UiLanguage);

        string tierName = (english, tier) switch
        {
            (true, ToolTier.Flint) => "FLINT",
            (true, ToolTier.Stone) => "STONE",
            (true, ToolTier.Iron) => "IRON",
            (true, ToolTier.Diamond) => "DIAMOND",
            (false, ToolTier.Flint) => "PAZOURKOVY",
            (false, ToolTier.Stone) => "KAMENNY",
            (false, ToolTier.Iron) => "ZELEZNY",
            (false, ToolTier.Diamond) => "DIAMANTOVY",
            _ => string.Empty,
        };
        string toolName = (english, tool) switch
        {
            (true, ToolKind.Pickaxe) => "PICKAXE",
            (true, ToolKind.Axe) => "AXE",
            (true, ToolKind.Shovel) => "SHOVEL",
            (false, ToolKind.Pickaxe) => "KRUMPAC",
            (false, ToolKind.Axe) => "SEKERU",
            (false, ToolKind.Shovel) => "LOPATU",
            _ => "TOOL",
        };

        return english
            ? $"REQUIRES: {tierName} {toolName}"
            : $"VYZADUJE: {tierName} {toolName}";
    }

    private sealed class WindowModCommandOutput(List<string> messages)
        : Tesseris.ModApi.IModCommandOutput
    {
        public void Reply(string text) => Add(text);

        public void Error(string text) => Add("ERROR: " + text);

        private void Add(string text)
        {
            if (!string.IsNullOrWhiteSpace(text))
            {
                messages.Add(text.Trim());
            }
        }
    }

    private bool HoldingPowerTool()
    {
        string? id = SelectedItemId();
        return id is "tesseris:overhead_wire_coil" or "tesseris:wire_cutters"
            || id?.StartsWith("tesseris:surface_cable_", StringComparison.Ordinal) == true;
    }

    private string? SelectedItemId()
    {
        if (_inventory is null || _items is null || _inventory.SelectedStack.IsEmpty)
        {
            return null;
        }

        return _items.Definition(_inventory.SelectedStack.Item).Id;
    }

    /// <summary>Pouzije civku nebo stipacky a zabrani, aby stejny klik zaroven tezel blok.</summary>
    private bool TryUsePowerTool(MicroHit hit, bool breakPressed, bool placePressed)
    {
        if (_power is null || _world is null || _inventory is null || _items is null)
        {
            return false;
        }

        string? id = SelectedItemId();
        if (id is null)
        {
            return false;
        }

        if (id == "tesseris:wire_cutters")
        {
            if (!breakPressed && !placePressed)
            {
                return true;
            }

            SurfaceCable[] removedSurface = _power.TakeSurface(hit.Block, hit.Face);
            OverheadWire[] removedOverhead = removedSurface.Length == 0
                ? _power.TakeOverheadAt(hit.Block)
                : [];
            int removedCount = removedSurface.Length + removedOverhead.Length;
            if (removedCount > 0)
            {
                _pendingWireEndpoint = null;
                SpawnRemovedCableDrops(removedSurface, removedOverhead, hit.Block);
                if (!_creative)
                {
                    _inventory.DamageSelected(1);
                }
                SetPowerNotice(removedCount == 1
                    ? "Kabel odstraněn; materiál vyskočil do světa."
                    : $"Odstraněno {removedCount} kabelů; materiál vyskočil do světa.");
            }

            return true;
        }

        if (TryCableColor(id, out CableColor color))
        {
            if (!placePressed)
            {
                return true;
            }

            ushort block = _world.GetBlock(hit.Block.X, hit.Block.Y, hit.Block.Z);
            if (!_world.Registry.IsSolid(block))
            {
                return true;
            }

            bool added = _power.AddSurface(hit.Block, hit.Face, color);
            if (!added)
            {
                SetPowerNotice($"{CableName(color)} kabel už na této ploše leží; zpět ho vezmou jen kleště.");
                return true;
            }

            if (!_creative && !_inventory.ConsumeSelected())
            {
                _power.RemoveSurface(hit.Block, hit.Face, color);
                return true;
            }

            SetPowerNotice($"Položen {CableName(color)} kabel, kanál {(int)color + 1}.");
            return true;
        }

        if (id != "tesseris:overhead_wire_coil")
        {
            return false;
        }

        if (!placePressed)
        {
            return true;
        }

        if (!TryTerminalKind(hit.Block, out PowerTerminalKind kind))
        {
            LogWireClick("invalid-target", hit.Block, null, "not-terminal");
            SetPowerNotice(_pendingWireEndpoint is null
                ? "Vodič musí končit na přípojce, rozvodce, transformátoru nebo stroji."
                : "Tohle není svorka; první vybraná svorka zůstává aktivní.");
            return true;
        }

        if (_pendingWireEndpoint is null)
        {
            if (!_power.HasFreePort(hit.Block, kind))
            {
                LogWireClick("first", hit.Block, kind, "full-rejected");
                SetPowerNotice($"Tahle svorka je plná ({_power.Degree(hit.Block)}/{PowerNetwork.CapacityOf(kind)}); první konec nebyl vybrán.");
                return true;
            }

            _pendingWireEndpoint = hit.Block;
            LogWireClick("first", hit.Block, kind, "selected");
            SetPowerNotice($"První svorka {hit.Block.X}/{hit.Block.Y}/{hit.Block.Z}; teď klikni na druhou.");
            return true;
        }

        Vector3i first = _pendingWireEndpoint.Value;
        if (first == hit.Block)
        {
            LogWireClick("second", hit.Block, kind, "cancelled-same-endpoint");
            _pendingWireEndpoint = null;
            SetPowerNotice("Stavění vedení zrušeno.");
            return true;
        }

        if (!TryTerminalKind(first, out PowerTerminalKind firstKind))
        {
            LogWireClick("second", hit.Block, kind, "first-terminal-disappeared");
            _pendingWireEndpoint = null;
            SetPowerNotice("První svorka mezitím zmizela; výběr byl zrušen.");
            return true;
        }

        OverheadAddResult result = _power.AddOverhead(first, hit.Block, firstKind, kind);
        LogWireClick("second", hit.Block, kind, result.ToString());
        _pendingWireEndpoint = null;

        if (result != OverheadAddResult.Added)
        {
            SetPowerNotice($"{OverheadFailureText(result)} Výběr byl zrušen; zvol první svorku znovu.");
            return true;
        }

        if (!_creative && !_inventory.ConsumeSelected())
        {
            _power.RemoveOverhead(first, hit.Block);
            return true;
        }

        SetPowerNotice("Venkovní vodič natažen.");
        return true;
    }

    private bool TryTerminalKind(Vector3i block, out PowerTerminalKind kind)
    {
        kind = PowerTerminalKind.Connector;
        if (_world is null)
        {
            return false;
        }

        string id = _world.Registry.Definition(_world.GetBlock(block.X, block.Y, block.Z)).Id;
        switch (id)
        {
            case "tesseris:power_connector":
                kind = PowerTerminalKind.Connector;
                return true;
            case "tesseris:power_relay":
                kind = PowerTerminalKind.Relay;
                return true;
            case "tesseris:power_transformer":
                kind = PowerTerminalKind.Transformer;
                return true;
            case "tesseris:furnace":
            case "tesseris:alloy_smelter":
            case "tesseris:battery_rack_large":
            case "tesseris:battery_rack_small":
            case "tesseris:metal_press":
            case "tesseris:ore_crusher":
            case "tesseris:quarry":
            case "tesseris:solar_panel":
            case "tesseris:wind_turbine":
                kind = PowerTerminalKind.Machine;
                return true;
            default:
                return false;
        }
    }

    private static bool TryCableColor(string id, out CableColor color)
    {
        color = id switch
        {
            "tesseris:surface_cable_red" => CableColor.Red,
            "tesseris:surface_cable_blue" => CableColor.Blue,
            "tesseris:surface_cable_yellow" => CableColor.Yellow,
            "tesseris:surface_cable_green" => CableColor.Green,
            _ => (CableColor)byte.MaxValue,
        };
        return color != (CableColor)byte.MaxValue;
    }

    private static string CableName(CableColor color) => color switch
    {
        CableColor.Red => "červený",
        CableColor.Blue => "modrý",
        CableColor.Yellow => "žlutý",
        CableColor.Green => "zelený",
        _ => "barevný",
    };

    private void SpawnRemovedCableDrops(
        IReadOnlyList<SurfaceCable> surface,
        IReadOnlyList<OverheadWire> overhead,
        Vector3i hitBlock)
    {
        if (_drops is null || _items is null)
        {
            return;
        }

        foreach (SurfaceCable cable in surface)
        {
            int item = _items.IndexOf(CableItemId(cable.Color));
            if (item == ItemRegistry.Nothing)
            {
                continue;
            }

            uint hash = (uint)HashCode.Combine(
                cable.Support.X, cable.Support.Y, cable.Support.Z, cable.Face, cable.Color);
            Vector3i faceNormal = PowerNetwork.FaceNormal(cable.Face);
            Vector3 normal = new(faceNormal.X, faceNormal.Y, faceNormal.Z);
            Vector3 faceCentre = new(
                cable.Support.X + 0.5f,
                cable.Support.Y + 0.5f,
                cable.Support.Z + 0.5f);
            faceCentre += normal * 0.72f;
            Vector3 scatter = CableDropScatter(hash);
            _drops.Spawn(
                new ItemStack(item, 1, 0),
                faceCentre + (scatter * 0.08f),
                (normal * 1.35f) + new Vector3(scatter.X, 1.9f, scatter.Z));
        }

        int coil = _items.IndexOf("tesseris:overhead_wire_coil");
        if (coil != ItemRegistry.Nothing)
        {
            for (int index = 0; index < overhead.Count; index++)
            {
                OverheadWire wire = overhead[index];
                uint hash = (uint)HashCode.Combine(
                    wire.A.X, wire.A.Y, wire.A.Z, wire.B.X, wire.B.Y, wire.B.Z, index);
                Vector3 scatter = CableDropScatter(hash);
                _drops.Spawn(
                    new ItemStack(coil, 1, 0),
                    new Vector3(hitBlock.X + 0.5f, hitBlock.Y + 1.2f, hitBlock.Z + 0.5f),
                    new Vector3(scatter.X, 2.25f, scatter.Z));
            }
        }

        _drops.Merge();
    }

    private void DropPowerCablesFromBlock(Vector3i block)
    {
        if (_power is null)
        {
            return;
        }

        SurfaceCable[] surface = _power.TakeSurfaceAtBlock(block);
        OverheadWire[] overhead = _power.TakeOverheadAt(block);
        if (surface.Length + overhead.Length > 0)
        {
            SpawnRemovedCableDrops(surface, overhead, block);
        }
    }

    private static string CableItemId(CableColor color) => color switch
    {
        CableColor.Red => "tesseris:surface_cable_red",
        CableColor.Blue => "tesseris:surface_cable_blue",
        CableColor.Yellow => "tesseris:surface_cable_yellow",
        CableColor.Green => "tesseris:surface_cable_green",
        _ => string.Empty,
    };

    private static Vector3 CableDropScatter(uint hash)
    {
        float x = (((hash >> 3) & 0xFFu) / 255f - 0.5f) * 1.4f;
        float z = (((hash >> 11) & 0xFFu) / 255f - 0.5f) * 1.4f;
        return new Vector3(x, 0f, z);
    }

    private void SetPowerNotice(string message)
    {
        _powerNotice = message;
        _powerNoticeSeconds = 5f;
        Log.Info(message);
    }

    private void LogWireClick(
        string phase,
        Vector3i hit,
        PowerTerminalKind? hitKind,
        string result)
    {
        string pending = _pendingWireEndpoint is { } first
            ? $"{first.X}/{first.Y}/{first.Z}"
            : "none";
        string hitPorts = hitKind is { } kind && _power is not null
            ? $"{_power.Degree(hit)}/{PowerNetwork.CapacityOf(kind)}"
            : "n/a";
        string firstPorts = _pendingWireEndpoint is { } selected
            && _power is not null
            && TryTerminalKind(selected, out PowerTerminalKind firstKind)
                ? $"{_power.Degree(selected)}/{PowerNetwork.CapacityOf(firstKind)}"
                : "n/a";

        Log.Info(
            $"PWR_WIRE phase={phase} pending={pending} pendingPorts={firstPorts} "
            + $"hit={hit.X}/{hit.Y}/{hit.Z} hitKind={hitKind?.ToString() ?? "none"} "
            + $"hitPorts={hitPorts} result={result}");
    }

    private static string OverheadFailureText(OverheadAddResult result) => result switch
    {
        OverheadAddResult.SameEndpoint => "Oba konce jsou stejná svorka.",
        OverheadAddResult.TooLong => "Vedení je delší než 48 bloků.",
        OverheadAddResult.Duplicate => "Mezi těmito svorkami už vedení existuje.",
        OverheadAddResult.FirstFull => "První svorka je už plná.",
        OverheadAddResult.SecondFull => "Druhá svorka je už plná.",
        _ => "Vedení nelze přidat.",
    };

    private void RefreshPowerMesh()
    {
        if (_power is null || _chunkRenderer is null || _overheadWireLayer < 0 || _junctionBoxLayer < 0
            || _powerCableLayers.Length < PowerNetwork.MaxLanesPerFace)
        {
            return;
        }

        if (_powerMeshRevision != _power.Revision)
        {
            PowerMeshBuilder.Build(
                _power, _powerMesh, _powerCableLayers, _overheadWireLayer, _junctionBoxLayer);
            _powerMeshRevision = _power.Revision;
        }

        _chunkRenderer.PowerMesh = _powerMesh;
    }

    private void HandleMining()
    {
        if (_world is null || _streamer is null || _mining is null || _inventory is null
            || _items is null || _drops is null)
        {
            return;
        }

        if (!MicroRaycast.Cast(
            _world, _player.EyePosition, _camera.Forward, ReachDistance, out MicroHit hit, hitLiquid: false))
        {
            _mining.Stop();
            return;
        }

        ushort block = _world.GetBlock(hit.Block.X, hit.Block.Y, hit.Block.Z);

        // VODU RUKOU NE, viz kbelík.
        if (_world.Registry.IsLiquid(block) || block == BlockRegistry.Air)
        {
            _mining.Stop();
            return;
        }

        if (!_mining.Update(hit.Block, block, _inventory.SelectedStack, _frameTimer.DeltaSeconds, _creative))
        {
            return;
        }

        if (RaiseBlockAction(
                Tesseris.ModApi.ModBlockActionPhase.Before,
                Tesseris.ModApi.ModBlockActionKind.Break,
                hit.Block,
                block))
        {
            _mining.Stop();
            return;
        }

        DispatchBlockLifecycle(
            Tesseris.ModApi.ModBlockLifecycleKind.Breaking,
            block,
            hit.Block,
            HeldModItemId());

        if (TryGetMaterial(hit.Block, out BlockMaterial broken))
        {
            _sounds?.Play(broken, BlockAction.Break, hit.Block);
        }

        bool drops = _mining.Drops && !_creative;

        // Dva slaby v jednom voxelu zůstávají dvě samostatné poloviny.
        if (TryRemoveStackedSlabHalf(hit, block, drops))
        {
            RaiseBlockAction(
                Tesseris.ModApi.ModBlockActionPhase.After,
                Tesseris.ModApi.ModBlockActionKind.Break,
                hit.Block,
                block);
            if (!_creative && _inventory.DamageSelected(1))
            {
                _sounds?.Play(BlockMaterial.Wood, BlockAction.Break, hit.Block);
            }
            _mining.Stop();
            return;
        }

        // BLOK ROZDĚLENÝ NA DÍLKY SE BOURÁ PO DÍLCÍCH, viz původní chování.
        bool buildingPart = _world.Registry.Definition(block).Pieces != PieceMask.Full;
        if (!buildingPart && _world.RemovePiece(hit.Block, hit.Position, hit.Normal))
        {
            if (_world.GetBlock(hit.Block.X, hit.Block.Y, hit.Block.Z) == BlockRegistry.Air)
            {
                DropPowerCablesFromBlock(hit.Block);
            }

            if (_felling?.IsLog(block) == true)
            {
                _ecology?.ForgetTree(hit.Block);
            }

            DropPlantsAbove(hit.Block);
            _streamer.InvalidateInteractiveBlock(hit.Block.X, hit.Block.Y, hit.Block.Z);
            RaiseBlockAction(
                Tesseris.ModApi.ModBlockActionPhase.After,
                Tesseris.ModApi.ModBlockActionKind.Break,
                hit.Block,
                block);
            if (_world.GetBlock(hit.Block.X, hit.Block.Y, hit.Block.Z) != block)
            {
                DispatchRuntimeBlockChange(
                    hit.Block,
                    block,
                    _world.GetBlock(hit.Block.X, hit.Block.Y, hit.Block.Z),
                    HeldModItemId());
            }
            return;
        }

        // PODETNUTÍ KMENE PORAZÍ CELÝ STROM. Kácet kláda po kládě znamená vyšplhat se
        // k vršku a hlavně to neodpovídá tomu, co člověk od poražení stromu čeká.
        if (drops && _felling is not null && _felling.IsLog(block)
            && _felling.Fell(_world, hit.Block, _drops, _streamer) > 0)
        {
            _ecology?.ForgetTree(hit.Block);
            _fluid?.Touch(hit.Block.X, hit.Block.Y, hit.Block.Z);
        }
        else
        {
            DropPowerCablesFromBlock(hit.Block);
            RemoveDoorMate(hit.Block, _world.Registry.Definition(block).Id);
            DetachChestPair(hit.Block, block);
            _world.SetBlock(hit.Block.X, hit.Block.Y, hit.Block.Z, BlockRegistry.Air);
            _world.SetMicro(hit.Block.X, hit.Block.Y, hit.Block.Z, null);
            NotifyColonyOfBlockChange(hit.Block.X, hit.Block.Y, hit.Block.Z);
            if (_felling?.IsLog(block) == true)
            {
                _ecology?.ForgetTree(hit.Block);
            }

            DropPlantsAbove(hit.Block);
            _streamer.InvalidateInteractiveBlock(hit.Block.X, hit.Block.Y, hit.Block.Z);
            _fluid?.Touch(hit.Block.X, hit.Block.Y, hit.Block.Z);

            if (!drops)
            {
                _ = _furnaces?.Remove(hit.Block);
                _ = _chests?.Remove(hit.Block);
            }

            if (drops)
            {
                // Rozbitá pec vysype, co v ní bylo. Zahodit to by znamenalo, že hráč přijde
                // o rozdělané tavení jen proto, že si pec přesunul.
                if (_furnaces is not null)
                {
                    foreach (ItemStack left in _furnaces.Remove(hit.Block))
                    {
                        if (!left.IsEmpty)
                        {
                            _drops!.SpawnFromBlock(left, hit.Block, 7u);
                        }
                    }
                }

                // Listí nedává sebe, ale to, co v něm bylo — viz TreeFelling.HandPick.
                if (_chests is not null)
                {
                    ItemStack[] contents = [.. _chests.Remove(hit.Block)];
                    for (int slot = 0; slot < contents.Length; slot++)
                    {
                        ItemStack left = contents[slot];
                        if (left.IsEmpty) continue;
                        uint hash = (uint)HashCode.Combine(
                            hit.Block.X, hit.Block.Y, hit.Block.Z, slot, left.Item);
                        _drops!.SpawnFromContainer(left, hit.Block, hash);
                    }
                }

                if (_felling is not null && _felling.IsLeaves(block))
                {
                    _felling.HandPick(block, hit.Block, _drops!);
                }
                else
                {
                    SpawnDrop(block, hit.Block);
                }
            }
        }

        RaiseBlockAction(
            Tesseris.ModApi.ModBlockActionPhase.After,
            Tesseris.ModApi.ModBlockActionKind.Break,
            hit.Block,
            block);

        DispatchRuntimeBlockChange(
            hit.Block,
            block,
            _world.GetBlock(hit.Block.X, hit.Block.Y, hit.Block.Z),
            HeldModItemId());

        // Opotřebení až po úspěšném rozbití. Kdyby se počítalo při kopání, ubývalo by
        // i z bloku, který hráč nakonec nedotěžil.
        if (!_creative && _inventory.DamageSelected(1))
        {
            _sounds?.Play(BlockMaterial.Wood, BlockAction.Break, hit.Block);
        }
    }

    /// <summary>Pošle modům běžnou hráčskou akci s blokem a vrátí zrušení before fáze.</summary>
    private bool RaiseBlockAction(
        Tesseris.ModApi.ModBlockActionPhase phase,
        Tesseris.ModApi.ModBlockActionKind kind,
        Vector3i at,
        ushort block)
    {
        if (_mods is null || _registry is null || block >= _registry.Count)
        {
            return false;
        }

        Tesseris.ModApi.ResourceId? heldItem = HeldModItemId();

        var context = new ModBlockActionContext(
            phase,
            kind,
            at.X,
            at.Y,
            at.Z,
            new Tesseris.ModApi.ResourceId(_registry.Definition(block).Id),
            heldItem);

        _mods.Events.RaiseBlockAction(context);
        return context.Cancel;
    }

    private Tesseris.ModApi.ResourceId? HeldModItemId()
    {
        int selected = _inventory?.SelectedStack.Item ?? ItemRegistry.Nothing;
        return _items is not null && selected != ItemRegistry.Nothing
            && selected >= 0 && selected < _items.Count
                ? new Tesseris.ModApi.ResourceId(_items.Definition(selected).Id)
                : null;
    }

    private void ResolveModBehaviorTargets()
    {
        _modLifecycleBlocks.Clear();
        _modIntervalBlocks.Clear();
        if (_mods is null || _registry is null)
        {
            return;
        }

        foreach (Tesseris.ModApi.ResourceId id in _mods.Behaviors.LifecycleTargetBlockIds)
        {
            if (_registry.TryIndexOf(id.Value, out ushort block))
            {
                _modLifecycleBlocks.Add(block);
            }
        }

        foreach (Tesseris.ModApi.ResourceId id in _mods.Behaviors.IntervalTargetBlockIds)
        {
            if (_registry.TryIndexOf(id.Value, out ushort block))
            {
                _modIntervalBlocks.Add(block);
            }
        }
    }

    private void OnModChunkLoaded(Vector3i chunkPosition, Chunk chunk) =>
        DispatchModChunkLifecycle(chunkPosition, chunk, loaded: true);

    private void OnModChunkUnloading(Vector3i chunkPosition, Chunk chunk) =>
        DispatchModChunkLifecycle(chunkPosition, chunk, loaded: false);

    private void DispatchModChunkLifecycle(Vector3i chunkPosition, Chunk chunk, bool loaded)
    {
        if (_mods is null || _registry is null
            || (_modLifecycleBlocks.Count == 0 && _modIntervalBlocks.Count == 0))
        {
            return;
        }

        int baseX = chunkPosition.X * Chunk.Size;
        int baseY = chunkPosition.Y * Chunk.Size;
        int baseZ = chunkPosition.Z * Chunk.Size;
        for (int y = 0; y < Chunk.Size; y++)
        {
            for (int z = 0; z < Chunk.Size; z++)
            {
                for (int x = 0; x < Chunk.Size; x++)
                {
                    ushort block = chunk.GetBlock(x, y, z);
                    if (!_modLifecycleBlocks.Contains(block) && !_modIntervalBlocks.Contains(block))
                    {
                        continue;
                    }

                    var position = new Tesseris.ModApi.ModBlockPosition(baseX + x, baseY + y, baseZ + z);
                    var id = new Tesseris.ModApi.ResourceId(_registry.Definition(block).Id);
                    if (_modLifecycleBlocks.Contains(block))
                    {
                        _mods.Behaviors.DispatchBlockLifecycle(
                            loaded
                                ? Tesseris.ModApi.ModBlockLifecycleKind.Loaded
                                : Tesseris.ModApi.ModBlockLifecycleKind.Unloaded,
                            id,
                            position,
                            causingItemId: null,
                            _modTick);
                    }
                    else if (loaded)
                    {
                        _mods.Behaviors.ActivateIntervalPosition(id, position);
                    }
                    else
                    {
                        _mods.Behaviors.DeactivateIntervalPosition(id, position);
                    }
                }

            }
        }
    }

    private void OnModGameBlockChanged(
        Tesseris.ModApi.ModBlockPosition position,
        Tesseris.ModApi.ResourceId previous,
        Tesseris.ModApi.ResourceId current)
    {
        if (_registry is null)
        {
            return;
        }

        ushort oldBlock = _registry.IndexOf(previous.Value);
        ushort newBlock = _registry.IndexOf(current.Value);
        DispatchRuntimeBlockChange(
            new Vector3i(position.X, position.Y, position.Z),
            oldBlock,
            newBlock,
            HeldModItemId());
    }

    private void DispatchRuntimeBlockChange(
        Vector3i at,
        ushort previous,
        ushort current,
        Tesseris.ModApi.ResourceId? causingItem)
    {
        if (previous == current || _mods is null || _registry is null)
        {
            return;
        }

        if (previous != BlockRegistry.Air && previous < _registry.Count)
        {
            DispatchBlockLifecycle(
                Tesseris.ModApi.ModBlockLifecycleKind.Broken,
                previous,
                at,
                causingItem);
        }

        if (current != BlockRegistry.Air && current < _registry.Count)
        {
            DispatchBlockLifecycle(
                Tesseris.ModApi.ModBlockLifecycleKind.Placed,
                current,
                at,
                causingItem);
        }
    }

    private void DispatchBlockLifecycle(
        Tesseris.ModApi.ModBlockLifecycleKind kind,
        ushort block,
        Vector3i at,
        Tesseris.ModApi.ResourceId? causingItem)
    {
        if (_mods is null || _registry is null || block >= _registry.Count)
        {
            return;
        }

        _mods.Behaviors.DispatchBlockLifecycle(
            kind,
            new Tesseris.ModApi.ResourceId(_registry.Definition(block).Id),
            new Tesseris.ModApi.ModBlockPosition(at.X, at.Y, at.Z),
            causingItem,
            _modTick);
    }

    /// <summary>Vysype obsah bloku na jeho místo.</summary>
    private void SpawnDrop(ushort block, Vector3i position)
    {
        BlockDefinition definition = _world!.Registry.Definition(block);
        int count = Math.Max(1, definition.DropCount);
        int item = string.IsNullOrWhiteSpace(definition.DropItem)
            ? _items!.ItemForBlock(DropOf(block))
            : _items!.IndexOf(definition.DropItem);

        if (item == ItemRegistry.Nothing)
        {
            return;
        }

        uint hash = (uint)HashCode.Combine(position.X, position.Y, position.Z);

        _drops!.SpawnFromBlock(new ItemStack(item, count, 0), position, hash);
        _drops.Merge();
    }

    /// <summary>
    /// Co po sobě blok zanechá. Většina bloků sebe, pár jich něco jiného.
    /// </summary>
    /// <remarks>
    /// Kámen dá dlažbu, ne kámen — je to jediné, co dělá z pece a kamenných nástrojů
    /// něco, co se musí vyrobit. Rudy zatím dávají samy sebe; tavení je řeší dál.
    /// </remarks>
    private ushort DropOf(ushort block)
    {
        BlockRegistry registry = _world!.Registry;

        ushort stone = registry.IndexOf("tesseris:stone");
        ushort grass = registry.IndexOf("tesseris:grass");

        if (block == stone)
        {
            return registry.IndexOf("tesseris:cobblestone");
        }

        // Z trávy zbude hlína. Travnatý blok je tráva narostlá na hlíně, ne materiál.
        return block == grass ? registry.IndexOf("tesseris:dirt") : block;
    }

    private bool TryAttackMob()
    {
        if (_world is null || _animals is null || _items is null || _inventory is null)
            return false;

        float reach = ReachDistance;
        if (MicroRaycast.Cast(
            _world,
            _player.EyePosition,
            _camera.Forward,
            ReachDistance,
            out MicroHit obstruction))
        {
            reach = Math.Max(0f, obstruction.Distance - 0.01f);
        }

        if (!_animals.HasTarget(_player.EyePosition, _camera.Forward, reach))
            return false;

        // The active hand swing is also the melee cooldown. Keep consuming primary input while it
        // runs, otherwise the block behind the mob would start being mined between two attacks.
        if (_swing > 0f)
            return true;

        ItemStack held = _inventory.SelectedStack;
        if (!_animals.TryHit(
            _world,
            _player.EyePosition,
            _camera.Forward,
            reach,
            AttackDamage(held),
            out MobHit hit))
        {
            return false;
        }

        StartSwing();
        if (hit.Kind == AnimalKind.Sheep && _audio is not null)
        {
            _audio.PlayAt(
                hit.Killed ? _animaliaSheepDeath : _animaliaSheepHurt,
                hit.Position,
                volume: 0.85f,
                pitch: 0.96f + ((hit.EntityId & 3L) * 0.025f));
        }
        else if (hit.Kind == AnimalKind.Deer && _audio is not null)
        {
            _audio.PlayAt(
                hit.Killed ? _animaliaReindeerDeath : _animaliaReindeerHurt,
                hit.Position,
                volume: 0.65f,
                pitch: 0.96f + ((hit.EntityId & 3L) * 0.025f));
        }
        if (!_creative && !held.IsEmpty)
            _inventory.DamageSelected(1);

        if (hit.Killed && hit.LootCount > 0 && !string.IsNullOrEmpty(hit.LootItem) && _drops is not null)
        {
            int loot = _items.IndexOf(hit.LootItem);
            if (loot != ItemRegistry.Nothing)
            {
                uint variation = unchecked((uint)hit.EntityId * 0x9E3779B9u);
                float sideways = (((variation >> 8) & 255u) / 255f - 0.5f) * 0.8f;
                _drops.Spawn(
                    new ItemStack(loot, hit.LootCount, 0),
                    hit.Position,
                    new Vector3(sideways, 2.1f, -sideways));
            }
        }

        return true;
    }

    private float AttackDamage(ItemStack stack)
    {
        if (stack.IsEmpty || _items is null)
            return 1f;

        ItemDefinition definition = _items.Definition(stack.Item);
        if (definition.Id.EndsWith("_sword", StringComparison.Ordinal))
        {
            return definition.Tier switch
            {
                ToolTier.Flint => 4f,
                ToolTier.Stone => 5f,
                ToolTier.Iron => 6f,
                ToolTier.Diamond => 7f,
                _ => 3f,
            };
        }

        if (definition.Tool == ToolKind.Axe)
            return 3f + (float)definition.Tier;

        return definition.Kind == ItemKind.Tool ? 2f : 1f;
    }

    private void HandleBlockEditing()
    {
        if (_world is null || _streamer is null || _hotbar is null)
        {
            return;
        }

        bool leftDown = MouseState.IsButtonDown(MouseButton.Left);
        bool rightDown = MouseState.IsButtonDown(MouseButton.Right);

        if (_inventoryScreen?.Open == true)
        {
            // Panel potřebuje vědět, kde je kurzor, kvůli popiskům pod myší.
            _inventoryScreen.Pointer = MouseState.Position;

            HandleInventoryMouse(leftDown, rightDown);

            _leftWasDown = leftDown;
            _rightWasDown = rightDown;
            _mining?.Stop();
            return;
        }

        bool breakPressed = leftDown && !_leftWasDown;
        bool placePressed = rightDown && !_rightWasDown;
        bool breakReleased = !leftDown && _leftWasDown;
        bool placeReleased = !rightDown && _rightWasDown;

        _leftWasDown = leftDown;
        _rightWasDown = rightDown;

        Tesseris.ModApi.ModInputPhase? primaryPhase = breakPressed
            ? Tesseris.ModApi.ModInputPhase.Pressed
            : leftDown
                ? Tesseris.ModApi.ModInputPhase.Held
                : breakReleased
                    ? Tesseris.ModApi.ModInputPhase.Released
                    : null;
        Tesseris.ModApi.ModInputPhase? secondaryPhase = placePressed
            ? Tesseris.ModApi.ModInputPhase.Pressed
            : rightDown
                ? Tesseris.ModApi.ModInputPhase.Held
                : placeReleased
                    ? Tesseris.ModApi.ModInputPhase.Released
                    : null;

        if (primaryPhase is { } primary
            && DispatchModUse(Tesseris.ModApi.ModUseKind.Primary, primary))
        {
            _mining?.Stop();
            return;
        }

        if (secondaryPhase is { } secondary
            && DispatchModUse(Tesseris.ModApi.ModUseKind.Secondary, secondary))
        {
            _mining?.Stop();
            return;
        }

        // Entity selection has priority over the block behind it. Holding primary repeats the attack
        // after the hand swing finishes and never starts mining through the creature.
        if (leftDown && TryAttackMob())
        {
            _mining?.Stop();
            return;
        }

        // TĚŽBA SE DRŽÍ, NEKLIKÁ. Dokud mizel blok na jedno kliknutí, neměla tvrdost bloku
        // ani nástroj v ruce na nic vliv — hlína i kámen stály jeden klik. Držení se pozná
        // podle 'leftDown', kdežto pokládání a dláto zůstávají na hranu stisku.
        if (leftDown && !_chiselMode && !HoldingBucket() && !HoldingPowerTool())
        {
            // Rozmach se opakuje, dokud se kope. Jeden úder na blok by se rozešel s tím,
            // že se blok kopáním ubírá plynule.
            StartSwing();
            HandleMining();
            return;
        }

        _mining?.Stop();

        if (!breakPressed && !placePressed)
        {
            return;
        }

        // Paprsek jde vždycky přes mikro úroveň: i v režimu bloků je potřeba trefit
        // otesaný blok správně, protože v blokové vrstvě je z něj vzduch.
        // TÝŽ PAPRSEK JAKO U OBRYSU, včetně toho, jestli si všímá vody. Kdyby se rozešly,
        // hráč by bouchl do něčeho jiného, než na co se mu rozsvítil rámeček.
        if (!MicroRaycast.Cast(
            _world, _player.EyePosition, _camera.Forward, ReachDistance, out MicroHit hit,
            hitLiquid: HoldingBucket()))
        {
            return;
        }

        // KBELÍK MÁ PŘEDNOST. Když ho hráč drží, neplatí ani dláto, ani běžná těžba —
        // v ruce má nádobu, ne nástroj.
        if (HoldingBucket())
        {
            UseBucket(hit, breakPressed);
            return;
        }

        if (TryUsePowerTool(hit, breakPressed, placePressed))
        {
            return;
        }

        if (_chiselMode)
        {
            ApplyChisel(hit, breakPressed);
            return;
        }

        if (breakPressed)
        {
            // VODU RUKOU NE. Kapalina se nedá vzít do dlaně a hlavně: až voda poteče, byla by
            // holá ruka nástroj na okamžité vysušení jezera jedním klikem. Jediná cesta, jak
            // vodu přenést, je kbelík.
            ushort block = _world.GetBlock(hit.Block.X, hit.Block.Y, hit.Block.Z);
            if (_world.Registry.IsLiquid(block))
            {
                return;
            }

            // Zvuk se přehraje ještě před odstraněním — potom už by materiál nešel zjistit.
            if (TryGetMaterial(hit.Block, out BlockMaterial broken))
            {
                _sounds?.Play(broken, BlockAction.Break, hit.Block);
            }

            // BLOK ROZDĚLENÝ NA DÍLKY SE BOURÁ PO DÍLCÍCH. Strom stojí na dvakrát jemnější
            // mřížce, takže odstranit celý blok by ubralo osm dílků najednou — hráč by kopl
            // do jednoho kousku kmene a zmizel by mu kus široký celý blok.
            if (TryRemoveStackedSlabHalf(hit, block, spawnDrop: false))
            {
                return;
            }

            BlockShape brokenShape = _world.Registry.ShapeOf(block);
            bool wholeShape = brokenShape is BlockShape.Stairs or BlockShape.Door
                or BlockShape.Trapdoor or BlockShape.Ladder or BlockShape.Chest or BlockShape.Torch;
            bool slab = _world.Registry.Definition(block).Id.EndsWith("_slab", StringComparison.Ordinal);
            if (!slab && !wholeShape && _world.RemovePiece(hit.Block, hit.Position, hit.Normal))
            {
                if (_world.GetBlock(hit.Block.X, hit.Block.Y, hit.Block.Z) == BlockRegistry.Air)
                {
                    DropPowerCablesFromBlock(hit.Block);
                }

                if (_felling?.IsLog(block) == true)
                {
                    _ecology?.ForgetTree(hit.Block);
                }

                DropPlantsAbove(hit.Block);
                _streamer.InvalidateInteractiveBlock(hit.Block.X, hit.Block.Y, hit.Block.Z);
                return;
            }

            DropPowerCablesFromBlock(hit.Block);
            _world.SetBlock(hit.Block.X, hit.Block.Y, hit.Block.Z, BlockRegistry.Air);
            _world.SetMicro(hit.Block.X, hit.Block.Y, hit.Block.Z, null);
            NotifyColonyOfBlockChange(hit.Block.X, hit.Block.Y, hit.Block.Z);
            if (brokenShape == BlockShape.Stairs) RefreshStairsAround(hit.Block);
            if (_felling?.IsLog(block) == true)
            {
                _ecology?.ForgetTree(hit.Block);
            }

            DropPlantsAbove(hit.Block);
            _streamer.InvalidateInteractiveBlock(hit.Block.X, hit.Block.Y, hit.Block.Z);

            // Voda kolem díry o ní sama neví - musí se probudit.
            _fluid?.Touch(hit.Block.X, hit.Block.Y, hit.Block.Z);
            return;
        }

        // PRAVÝM TLAČÍTKEM NA PEC SE PEC OTEVŘE, nepokládá se do ní blok. Rozhoduje to,
        // na co hráč míří, ne co drží — jinak by se pec nedala otevřít s materiálem v ruce.
        string interactedBlock = _world.Registry.Definition(
            _world.GetBlock(hit.Block.X, hit.Block.Y, hit.Block.Z)).Id;
        if (placePressed && TryToggleBuildingPart(hit.Block, interactedBlock)) return;
        if (placePressed && _chests is not null && _inventoryScreen is not null
            && interactedBlock is "tesseris:chest" or "tesseris:loot_chest")
        {
            _openChest = hit.Block;
            _openChestPartner = TryGetChestPartner(hit.Block, out Vector3i partner)
                ? partner
                : null;
            StartChestAnimation(hit.Block, targetOpen: true);
            if (_openChestPartner is { } paired)
            {
                StartChestAnimation(paired, targetOpen: true);
            }
            _inventoryScreen.Furnace = null;
            Chest clicked = ChestAt(hit.Block, interactedBlock);
            if (_openChestPartner is { } other)
            {
                string otherId = _world.Registry.Definition(
                    _world.GetBlock(other.X, other.Y, other.Z)).Id;
                Chest pairedChest = ChestAt(other, otherId);
                bool clickedIsLeft = PieceMask.ChestPair(
                    _world.GetPieces(hit.Block.X, hit.Block.Y, hit.Block.Z)) == ChestPairSide.Left;
                _inventoryScreen.Chest = clickedIsLeft
                    ? new ChestContainer(clicked, pairedChest)
                    : new ChestContainer(pairedChest, clicked);
            }
            else
            {
                _inventoryScreen.Chest = new ChestContainer(clicked);
            }
            _inventoryScreen.Open = true;
            CursorState = CursorState.Normal;
            return;
        }

        if (placePressed && _furnaces is not null && _inventoryScreen is not null
            && _world.Registry.Definition(_world.GetBlock(hit.Block.X, hit.Block.Y, hit.Block.Z)).Id
                == "tesseris:stone_furnace")
        {
            _openFurnace = hit.Block;
            _inventoryScreen.Chest = null;
            _inventoryScreen.Furnace = _furnaces.At(hit.Block);
            _inventoryScreen.Open = true;

            CursorState = CursorState.Normal;
            return;
        }

        // CO SE VLASTNĚ POKLÁDÁ. V obou režimech to, co má hráč ve vybraném slotu; v kreativu
        // se jen neubírá. Materiál se do inventáře bere z mřížky předmětů (E).
        ushort placed =
            _items?.BlockForItem(_inventory?.SelectedStack.Item ?? ItemRegistry.Nothing) ?? BlockRegistry.Air;

        if (placed == BlockRegistry.Air)
        {
            return;
        }

        string placedId = _world.Registry.Definition(placed).Id;
        bool mergingSlab = TryGetSlabMergeTarget(hit, placed, placedId, out Vector3i target);
        if (!mergingSlab)
        {
            target = _world.PlacementTarget(hit.Block, hit.Normal, placed);
        }

        // Blok se nesmí položit do hráče — zasekl by se v něm.
        if (_player.Overlaps(target) || target.Y < 0 || target.Y >= TerrainGenerator.WorldHeight)
        {
            return;
        }

        // VODA SE Z VÝBĚRU NEPOKLÁDÁ. Je v registru, takže by jinak šla postavit jako cihla -
        // a s tekoucí vodou by to znamenalo zaplavit svět bez jediné nádoby.
        if (_world.Registry.IsLiquid(placed))
        {
            return;
        }

        // Nejdřív se musí skutečně změnit svět. TryPlace odmítne obsazený voxel, stejný blok
        // i špatný podklad, takže se kus nikdy neodečte za neviditelný zápis nebo tichý no-op.
        if (!_creative && (_inventory?.SelectedStack.IsEmpty ?? true))
        {
            return;
        }

        if (placedId == "tesseris:wooden_door"
            && !_world.CanPlace(_registry!.IndexOf("tesseris:wooden_door_top"), target.X, target.Y + 1, target.Z))
        {
            return;
        }

        // Žebřík patří pouze na svislou stěnu. Hit block je současně jeho opora.
        if (placedId == "tesseris:ladder" && hit.Normal.Y != 0)
        {
            return;
        }

        // Pochoden muze stat na podlaze nebo viset na jedne ze ctyr sten. Na strop se
        // nepoklada a opora musi byt skutecny pevny blok, ne vzduch, voda ci rostlina.
        if (placedId == "tesseris:torch"
            && (hit.Normal.Y < 0 || !_world.Registry.IsSolid(
                _world.GetBlock(hit.Block.X, hit.Block.Y, hit.Block.Z))))
        {
            return;
        }

        if (RaiseBlockAction(
                Tesseris.ModApi.ModBlockActionPhase.Before,
                Tesseris.ModApi.ModBlockActionKind.Place,
                target,
                placed))
        {
            return;
        }

        ushort replaced;
        if (mergingSlab)
        {
            replaced = placed;
            if (!_world.SetPieces(target.X, target.Y, target.Z, PieceMask.Full)) return;
        }
        else if (!_world.TryPlace(placed, target.X, target.Y, target.Z, out replaced)) return;

        // Kolonisté musí o postaveném vědět, jinak jím procházejí.
        NotifyColonyOfBlockChange(target.X, target.Y, target.Z);

        if (placedId == "tesseris:wooden_door")
        {
            _world.TryPlace(_registry!.IndexOf("tesseris:wooden_door_top"), target.X, target.Y + 1, target.Z);
            int facing = PieceMask.DoorFacingFromDirection(_camera.Forward.X, _camera.Forward.Z);
            Vector3i right = PieceMask.DoorRightStep(facing);
            var centre = new Vector3(target.X + 0.5f, target.Y + 0.5f, target.Z + 0.5f);
            Vector3 lateral = hit.Position - centre;
            bool hingeRight = (lateral.X * right.X) + (lateral.Z * right.Z) >= 0f;

            if (TryFindDoorPlacementPartner(
                    target, facing, hingeRight, out Vector3i partner, out bool partnerOnRight))
            {
                // Levé dveře mají pant vlevo, pravé vpravo. Volné vnitřní hrany se tak
                // při otevření rozestoupí od sebe a vznikne skutečný dvojitý průchod.
                hingeRight = !partnerOnRight;
                ApplyDoorState(partner, PieceMask.DoorState(facing, partnerOnRight, open: false));
            }

            ApplyDoorState(target, PieceMask.DoorState(facing, hingeRight, open: false));
        }
        else if (_world.Registry.ShapeOf(placed) == BlockShape.Chest)
        {
            // Čelo bedny míří k hráči. Položení mění pouze rotaci a případné spojení se sousedem.
            int facing = PieceMask.DoorFacingFromDirection(-_camera.Forward.X, -_camera.Forward.Z);
            PairPlacedChest(target, placed, facing);
        }
        else if (placedId.EndsWith("_stairs", StringComparison.Ordinal))
        {
            int facing = PieceMask.DoorFacingFromDirection(_camera.Forward.X, _camera.Forward.Z);
            float localHitY = Math.Clamp(hit.Position.Y - MathF.Floor(hit.Position.Y), 0f, 1f);
            bool upsideDown = hit.Normal.Y < 0 || (hit.Normal.Y == 0 && localHitY > 0.5f);
            _world.SetPieces(
                target.X, target.Y, target.Z,
                PieceMask.StairState(facing, upsideDown, StairCornerShape.Straight));
            RefreshStairsAround(target);
        }
        else if (placedId.EndsWith("_slab", StringComparison.Ordinal) && !mergingSlab)
        {
            float localHitY = hit.Position.Y - MathF.Floor(hit.Position.Y);
            bool upper = hit.Normal.Y < 0 || (hit.Normal.Y == 0 && localHitY > 0.5f);
            _world.SetPieces(
                target.X, target.Y, target.Z,
                upper ? PieceMask.SlabTop : PieceMask.SlabBottom);
        }
        else if (placedId == "tesseris:wooden_trapdoor")
        {
            float localHitY = hit.Position.Y - MathF.Floor(hit.Position.Y);
            bool upper = hit.Normal.Y < 0 || (hit.Normal.Y == 0 && localHitY > 0.5f);
            bool alongZ = MathF.Abs(_camera.Forward.X) > MathF.Abs(_camera.Forward.Z);
            _world.SetPieces(
                target.X, target.Y, target.Z,
                PieceMask.TrapdoorClosed(upper, alongZ));
        }
        else if (placedId == "tesseris:ladder")
        {
            _world.SetPieces(
                target.X, target.Y, target.Z,
                PieceMask.LadderStateFromNormal(hit.Normal));
        }
        else if (placedId == "tesseris:torch")
        {
            _world.SetPieces(
                target.X, target.Y, target.Z,
                PieceMask.TorchStateFromNormal(hit.Normal));
        }
        else if (placedId == "tesseris:window_pane")
        {
            byte paneMask = MathF.Abs(_camera.Forward.X) > MathF.Abs(_camera.Forward.Z) ? (byte)85 : (byte)51;
            _world.SetPieces(target.X, target.Y, target.Z, paneMask);
        }
        else if (placedId == TownHall.BlockId)
        {
            // ZALOZENI KOLONIE. Timhle blokem kolonie vznika; do te doby nikdo neprijde.
            // Druha radnice kolonii nepresouva, jen se to hraci rekne.
            //
            // SKLAD PATRI K RADNICI, takze vznika stejnym krokem. Bez mista ve svete je sklad
            // jen obsah, ke kteremu se neda dojit — a hladovy clovek by nemel kam jit jist.
            if (_colony.FoundTownHall(target))
            {
                Log.Info($"Kolonie zalozena na {target}. Lide zacnou prichazet, sklad je u radnice.");
            }
            else
            {
                Log.Warn($"Kolonie uz stoji na {_colony.TownHall.Cell}. Druha radnice ji nepresune.");
            }
        }

        // V PŘEŽITÍ SE POLOŽENÝ BLOK ODEČTE. V kreativu ne — tam je paleta zdroj, ne zásoba.
        if (!_creative)
        {
            bool consumed = _inventory!.ConsumeSelected();
            Debug.Assert(consumed, "Vybraný blok zmizel mezi kontrolou a odečtením.");
        }

        _streamer.InvalidateInteractiveBlock(target.X, target.Y, target.Z);
        DropPlantsAbove(target);

        // Nahradit trávu blokem znamená trávu rozbít, ne ji beze stopy smazat. Drop vzniká
        // nad novým blokem; v původním voxelu by byl uvnitř jeho kolize a nešel by sebrat.
        if (!_creative
            && (_world.Registry.IsReplaceableVegetation(replaced)
                || _world.Registry.IsReplaceableGroundClutter(replaced))
            && _items is not null && _drops is not null)
        {
            SpawnDrop(replaced, target + Vector3i.UnitY);
        }

        _fluid?.Touch(target.X, target.Y, target.Z);

        RaiseBlockAction(
            Tesseris.ModApi.ModBlockActionPhase.After,
            Tesseris.ModApi.ModBlockActionKind.Place,
            target,
            placed);

        DispatchRuntimeBlockChange(target, replaced, placed, HeldModItemId());

        StartSwing();

        // ZASAZENÁ SAZENICE SE HLÁSÍ ROVNOU. Průchod okolí by ji našel taky, ale až za pár
        // vteřin — hráč by první sazenici zasadil a nic by se nedělo.
        if (_saplings is not null && _generator?.Trees?.IsSapling(placed) == true)
        {
            _saplings.Notice(target);
        }

        // ZVUK PODLE POLOŽENÉHO BLOKU, ne podle kreativní palety. Dokud se bral z palety,
        // znělo v přežití položení všeho jako materiál, který měl hráč vybraný v kreativu.
        _sounds?.Play(
            _world.Registry.Definition(placed).Material,
            BlockAction.Place,
            target);
    }

    private void RefreshStairsAround(Vector3i centre)
    {
        if (_world is null || _streamer is null) return;

        Span<Vector3i> positions = stackalloc Vector3i[5]
        {
            centre,
            centre + Vector3i.UnitX,
            centre - Vector3i.UnitX,
            centre + Vector3i.UnitZ,
            centre - Vector3i.UnitZ,
        };

        foreach (Vector3i position in positions)
        {
            RefreshStair(position);
        }
    }

    private void RefreshStair(Vector3i position)
    {
        if (_world is null || _streamer is null
            || !TryGetStair(position, out _, out int facing, out bool upsideDown))
        {
            return;
        }

        StairCornerShape corner = StairCornerShape.Straight;
        Vector3i forward = PieceMask.StairForwardStep(facing);
        int left = PieceMask.StairLeftFacing(facing);
        int right = (left + 2) & 0x03;

        if (TryGetStair(position + forward, out _, out int frontFacing, out bool frontUpside)
            && frontUpside == upsideDown)
        {
            if (frontFacing == left) corner = StairCornerShape.OuterLeft;
            else if (frontFacing == right) corner = StairCornerShape.OuterRight;
        }

        if (corner == StairCornerShape.Straight
            && TryGetStair(position - forward, out _, out int backFacing, out bool backUpside)
            && backUpside == upsideDown)
        {
            if (backFacing == left) corner = StairCornerShape.InnerLeft;
            else if (backFacing == right) corner = StairCornerShape.InnerRight;
        }

        byte current = _world.GetPieces(position.X, position.Y, position.Z);
        byte next = PieceMask.StairState(facing, upsideDown, corner);
        if (current == next || !_world.SetPieces(position.X, position.Y, position.Z, next)) return;
        _streamer.InvalidateInteractiveBlock(position.X, position.Y, position.Z);
    }

    private bool TryGetStair(
        Vector3i position,
        out ushort block,
        out int facing,
        out bool upsideDown)
    {
        block = BlockRegistry.Air;
        facing = PieceMask.DoorSouth;
        upsideDown = false;
        if (_world is null || position.Y < 0 || position.Y >= TerrainGenerator.WorldHeight) return false;

        block = _world.GetBlock(position.X, position.Y, position.Z);
        if (_world.Registry.ShapeOf(block) != BlockShape.Stairs) return false;

        byte state = _world.GetPieces(position.X, position.Y, position.Z);
        facing = PieceMask.StairFacing(state);
        upsideDown = PieceMask.StairUpsideDown(state);
        return true;
    }

    private bool TryGetSlabMergeTarget(
        MicroHit hit, ushort placed, string placedId, out Vector3i target)
    {
        target = hit.Block;
        if (_world is null || !placedId.EndsWith("_slab", StringComparison.Ordinal)
            || _world.GetBlock(hit.Block.X, hit.Block.Y, hit.Block.Z) != placed)
        {
            return false;
        }

        byte existing = _world.GetPieces(hit.Block.X, hit.Block.Y, hit.Block.Z);
        return (existing == PieceMask.SlabBottom && hit.Normal.Y > 0)
            || (existing == PieceMask.SlabTop && hit.Normal.Y < 0);
    }

    private bool TryRemoveStackedSlabHalf(MicroHit hit, ushort block, bool spawnDrop)
    {
        if (_world is null || _streamer is null
            || !_world.Registry.Definition(block).Id.EndsWith("_slab", StringComparison.Ordinal)
            || _world.GetPieces(hit.Block.X, hit.Block.Y, hit.Block.Z) != PieceMask.Full)
        {
            return false;
        }

        float localHitY = Math.Clamp(hit.Position.Y - hit.Block.Y, 0f, 1f);
        bool hitUpper = hit.Normal.Y < 0 ? false : hit.Normal.Y > 0 || localHitY >= 0.5f;
        byte remaining = hitUpper ? PieceMask.SlabBottom : PieceMask.SlabTop;
        if (!_world.SetPieces(hit.Block.X, hit.Block.Y, hit.Block.Z, remaining)) return false;

        if (spawnDrop) SpawnDrop(block, hit.Block);
        _streamer.InvalidateInteractiveBlock(hit.Block.X, hit.Block.Y, hit.Block.Z);
        _fluid?.Touch(hit.Block.X, hit.Block.Y, hit.Block.Z);
        return true;
    }

    private bool TryToggleBuildingPart(Vector3i block, string id)
    {
        if (_world is null || _streamer is null) return false;
        if (id == "tesseris:wooden_trapdoor")
        {
            byte current = _world.GetPieces(block.X, block.Y, block.Z);
            byte trapdoorNext = PieceMask.ToggleTrapdoor(current);
            StartTrapdoorAnimation(block, current, trapdoorNext);
            return true;
        }
        if (id is not ("tesseris:wooden_door" or "tesseris:wooden_door_top")) return false;

        Vector3i bottom = id.EndsWith("_top", StringComparison.Ordinal) ? block - Vector3i.UnitY : block;
        byte currentDoor = _world.GetPieces(bottom.X, bottom.Y, bottom.Z);
        int facing = PieceMask.DoorFacing(currentDoor);
        bool hingeRight = PieceMask.DoorHingeRight(currentDoor);
        bool nextOpen = !PieceMask.DoorIsOpen(currentDoor);
        StartDoorAnimation(bottom, currentDoor, nextOpen);

        if (TryFindPairedDoor(bottom, currentDoor, out Vector3i partner))
        {
            byte partnerState = _world.GetPieces(partner.X, partner.Y, partner.Z);
            StartDoorAnimation(partner, partnerState, nextOpen);
        }

        return true;
    }

    private void StartDoorAnimation(Vector3i bottom, byte currentState, bool targetOpen)
    {
        int facing = PieceMask.DoorFacing(currentState);
        bool hingeRight = PieceMask.DoorHingeRight(currentState);
        byte closed = PieceMask.DoorState(facing, hingeRight, open: false);
        byte open = PieceMask.DoorState(facing, hingeRight, open: true);
        byte target = targetOpen ? open : closed;

        if (!_buildingPartAnimations.TryGetValue(bottom, out ActiveBuildingPartAnimation? animation)
            || animation.Shape != BlockShape.Door)
        {
            animation = new ActiveBuildingPartAnimation
            {
                Shape = BlockShape.Door,
                LowerBlock = _world!.GetBlock(bottom.X, bottom.Y, bottom.Z),
                UpperBlock = _world.GetBlock(bottom.X, bottom.Y + 1, bottom.Z),
                ClosedState = closed,
                OpenState = open,
                OpenAmount = PieceMask.DoorIsOpen(currentState) ? 1f : 0f,
            };
            _buildingPartAnimations[bottom] = animation;
        }

        animation.TargetOpen = targetOpen;
        animation.Finalized = false;
        animation.HoldSeconds = 0f;
        animation.StartDelay = 0.055f;

        byte animated = PieceMask.DoorAnimatingState(target);
        _world!.SetPieces(bottom.X, bottom.Y, bottom.Z, animated);
        _world.SetPieces(bottom.X, bottom.Y + 1, bottom.Z, animated);
        _streamer!.InvalidateInteractiveBlock(bottom.X, bottom.Y, bottom.Z);
        _streamer.InvalidateInteractiveBlock(bottom.X, bottom.Y + 1, bottom.Z);
    }

    private void StartTrapdoorAnimation(Vector3i block, byte currentState, byte targetState)
    {
        byte current = PieceMask.TrapdoorFinalState(currentState);
        byte target = PieceMask.TrapdoorFinalState(targetState);
        byte closed = PieceMask.TrapdoorIsOpen(current) ? target : current;
        byte open = PieceMask.TrapdoorIsOpen(current) ? current : target;

        if (!_buildingPartAnimations.TryGetValue(block, out ActiveBuildingPartAnimation? animation)
            || animation.Shape != BlockShape.Trapdoor)
        {
            animation = new ActiveBuildingPartAnimation
            {
                Shape = BlockShape.Trapdoor,
                LowerBlock = _world!.GetBlock(block.X, block.Y, block.Z),
                UpperBlock = BlockRegistry.Air,
                ClosedState = closed,
                OpenState = open,
                OpenAmount = PieceMask.TrapdoorIsOpen(current) ? 1f : 0f,
            };
            _buildingPartAnimations[block] = animation;
        }

        animation.TargetOpen = PieceMask.TrapdoorIsOpen(target);
        animation.Finalized = false;
        animation.HoldSeconds = 0f;
        animation.StartDelay = 0.055f;

        _world!.SetPieces(
            block.X, block.Y, block.Z,
            PieceMask.TrapdoorAnimatingState(target));
        _streamer!.InvalidateInteractiveBlock(block.X, block.Y, block.Z);
    }

    private void StartChestAnimation(Vector3i block, bool targetOpen)
    {
        if (_world is null || _streamer is null) return;
        ushort blockId = _world.GetBlock(block.X, block.Y, block.Z);
        if (_world.Registry.ShapeOf(blockId) != BlockShape.Chest) return;

        byte state = _world.GetPieces(block.X, block.Y, block.Z);
        byte closedState = PieceMask.ChestState(
            PieceMask.ChestFacing(state), PieceMask.ChestPair(state), open: false);
        byte openState = PieceMask.ChestState(
            PieceMask.ChestFacing(state), PieceMask.ChestPair(state), open: true);
        if (!_buildingPartAnimations.TryGetValue(block, out ActiveBuildingPartAnimation? animation)
            || animation.Shape != BlockShape.Chest)
        {
            animation = new ActiveBuildingPartAnimation
            {
                Shape = BlockShape.Chest,
                LowerBlock = blockId,
                UpperBlock = BlockRegistry.Air,
                ClosedState = closedState,
                OpenState = openState,
                OpenAmount = PieceMask.ChestIsOpen(state) ? 1f : 0f,
            };
            _buildingPartAnimations[block] = animation;
        }

        animation.TargetOpen = targetOpen;
        animation.Finalized = false;
        animation.HoldSeconds = 0f;
        animation.StartDelay = 0f;
        _world.SetPieces(
            block.X, block.Y, block.Z,
            PieceMask.ChestAnimatingState(targetOpen ? openState : closedState, targetOpen));
        _streamer.InvalidateInteractiveBlock(block.X, block.Y, block.Z);
    }

    private void UpdateBuildingPartAnimations(float deltaSeconds)
    {
        if (_world is null || _streamer is null || _registry is null || _chunkRenderer is null)
        {
            return;
        }

        const float Duration = 0.24f;
        const float StaticMeshGrace = 0.75f;
        _buildingPartAnimationScratch.Clear();
        List<Vector3i>? finished = null;

        foreach ((Vector3i block, ActiveBuildingPartAnimation animation) in _buildingPartAnimations)
        {
            if (animation.StartDelay > 0f)
            {
                animation.StartDelay = MathF.Max(0f, animation.StartDelay - deltaSeconds);
            }
            else if (!animation.Finalized)
            {
                float target = animation.TargetOpen ? 1f : 0f;
                animation.OpenAmount = MoveTowards(
                    animation.OpenAmount, target, deltaSeconds / Duration);

                if (animation.OpenAmount == target)
                {
                    if (animation.Shape == BlockShape.Chest && animation.TargetOpen)
                    {
                        // Otevřená truhla zůstává v dynamickém rendereru po celou dobu,
                        // kdy je její inventář na obrazovce. Zavření ji plynule pošle zpět.
                        animation.Finalized = true;
                        animation.HoldSeconds = float.PositiveInfinity;
                        _world.SetPieces(block.X, block.Y, block.Z,
                            PieceMask.ChestAnimatingState(animation.OpenState, open: true));
                        _streamer.InvalidateInteractiveBlock(block.X, block.Y, block.Z);
                        goto AddAnimationMesh;
                    }

                    byte finalState = animation.TargetOpen
                        ? animation.OpenState
                        : animation.ClosedState;
                    if (animation.Shape == BlockShape.Door)
                    {
                        _world.SetPieces(block.X, block.Y, block.Z, finalState);
                        _world.SetPieces(block.X, block.Y + 1, block.Z, finalState);
                        _streamer.InvalidateInteractiveBlock(block.X, block.Y, block.Z);
                        _streamer.InvalidateInteractiveBlock(block.X, block.Y + 1, block.Z);
                    }
                    else
                    {
                        _world.SetPieces(block.X, block.Y, block.Z, finalState);
                        _streamer.InvalidateInteractiveBlock(block.X, block.Y, block.Z);
                    }

                    animation.Finalized = true;
                    animation.HoldSeconds = StaticMeshGrace;
                }
            }
            else
            {
                if (animation.Shape == BlockShape.Chest && animation.TargetOpen)
                {
                    goto AddAnimationMesh;
                }

                animation.HoldSeconds -= deltaSeconds;
                if (animation.HoldSeconds <= 0f)
                {
                    (finished ??= []).Add(block);
                    continue;
                }
            }

        AddAnimationMesh:
            _buildingPartAnimationScratch.Add(new BuildingPartAnimation(
                block,
                animation.LowerBlock,
                animation.UpperBlock,
                animation.Shape,
                animation.ClosedState,
                animation.OpenState,
                animation.OpenAmount));
        }

        if (finished is not null)
        {
            foreach (Vector3i block in finished)
            {
                _buildingPartAnimations.Remove(block);
            }
        }

        BuildingPartAnimationMesher.Build(
            _animatedBuildingPartMesh, _registry, _buildingPartAnimationScratch);
        _chunkRenderer.AnimatedBuildingParts = _animatedBuildingPartMesh;
    }

    private static float MoveTowards(float current, float target, float maximumDelta)
    {
        if (MathF.Abs(target - current) <= maximumDelta)
        {
            return target;
        }

        return current + (MathF.Sign(target - current) * maximumDelta);
    }

    private sealed class ActiveBuildingPartAnimation
    {
        public BlockShape Shape;
        public ushort LowerBlock;
        public ushort UpperBlock;
        public byte ClosedState;
        public byte OpenState;
        public float OpenAmount;
        public bool TargetOpen;
        public bool Finalized;
        public float HoldSeconds;
        public float StartDelay;
    }

    private Chest ChestAt(Vector3i position, string blockId) => blockId == "tesseris:loot_chest"
        ? _chests!.AtLoot(position, _generator?.Seed ?? 0)
        : _chests!.At(position);

    private void PairPlacedChest(Vector3i position, ushort blockId, int facing)
    {
        if (_world is null || _streamer is null) return;

        byte single = PieceMask.ChestState(facing, ChestPairSide.Single, open: false);
        _world.SetPieces(position.X, position.Y, position.Z, single);

        Vector3i right = PieceMask.DoorRightStep(facing);
        Vector3i rightCandidate = position + right;
        Vector3i leftCandidate = position - right;
        bool hasRight = CanPairChest(rightCandidate, blockId, facing);
        bool hasLeft = CanPairChest(leftCandidate, blockId, facing);

        // Tři bedny se do jedné řady neslévají. Pokud jsou sousedé po obou stranách,
        // nová zůstane samostatná a nerozbije už existující úložiště.
        if (hasRight == hasLeft) return;

        Vector3i partner = hasRight ? rightCandidate : leftCandidate;
        ChestPairSide ownSide = hasRight ? ChestPairSide.Left : ChestPairSide.Right;
        ChestPairSide partnerSide = hasRight ? ChestPairSide.Right : ChestPairSide.Left;
        _world.SetPieces(position.X, position.Y, position.Z,
            PieceMask.ChestState(facing, ownSide, open: false));
        _world.SetPieces(partner.X, partner.Y, partner.Z,
            PieceMask.ChestState(facing, partnerSide, open: false));
        _streamer.InvalidateInteractiveBlock(partner.X, partner.Y, partner.Z);
    }

    private bool CanPairChest(Vector3i position, ushort blockId, int facing)
    {
        if (_world is null || _world.GetBlock(position.X, position.Y, position.Z) != blockId)
        {
            return false;
        }

        byte state = _world.GetPieces(position.X, position.Y, position.Z);
        return PieceMask.ChestFacing(state) == facing
            && PieceMask.ChestPair(state) == ChestPairSide.Single
            && !PieceMask.ChestIsAnimating(state);
    }

    private bool TryGetChestPartner(Vector3i position, out Vector3i partner)
    {
        partner = default;
        if (_world is null) return false;
        ushort blockId = _world.GetBlock(position.X, position.Y, position.Z);
        if (_world.Registry.ShapeOf(blockId) != BlockShape.Chest) return false;

        byte state = _world.GetPieces(position.X, position.Y, position.Z);
        ChestPairSide side = PieceMask.ChestPair(state);
        if (side == ChestPairSide.Single) return false;

        int facing = PieceMask.ChestFacing(state);
        Vector3i right = PieceMask.DoorRightStep(facing);
        partner = side == ChestPairSide.Left ? position + right : position - right;
        if (_world.GetBlock(partner.X, partner.Y, partner.Z) != blockId) return false;

        byte partnerState = _world.GetPieces(partner.X, partner.Y, partner.Z);
        ChestPairSide expected = side == ChestPairSide.Left
            ? ChestPairSide.Right
            : ChestPairSide.Left;
        return PieceMask.ChestFacing(partnerState) == facing
            && PieceMask.ChestPair(partnerState) == expected;
    }

    private void DetachChestPair(Vector3i position, ushort blockId)
    {
        if (_world is null || _streamer is null
            || _world.Registry.ShapeOf(blockId) != BlockShape.Chest)
        {
            return;
        }

        if (!TryGetChestPartner(position, out Vector3i partner)) return;
        byte partnerState = _world.GetPieces(partner.X, partner.Y, partner.Z);
        _world.SetPieces(partner.X, partner.Y, partner.Z, PieceMask.ChestState(
            PieceMask.ChestFacing(partnerState), ChestPairSide.Single, open: false));
        _buildingPartAnimations.Remove(position);
        _buildingPartAnimations.Remove(partner);
        _streamer.InvalidateInteractiveBlock(partner.X, partner.Y, partner.Z);
    }

    private bool TryFindDoorPlacementPartner(
        Vector3i bottom,
        int facing,
        bool preferredHingeRight,
        out Vector3i partner,
        out bool partnerOnRight)
    {
        Vector3i right = PieceMask.DoorRightStep(facing);
        Vector3i rightCandidate = bottom + right;
        Vector3i leftCandidate = bottom - right;
        bool hasRight = IsCompleteDoorFacing(rightCandidate, facing);
        bool hasLeft = IsCompleteDoorFacing(leftCandidate, facing);

        if (!hasRight && !hasLeft)
        {
            partner = default;
            partnerOnRight = false;
            return false;
        }

        // Pokud jsou dveře po obou stranách, respektuje se strana pantu zvolená bodem
        // kliknutí. Jinak se automaticky vezmou jediné sousední dveře.
        partnerOnRight = hasRight && (!hasLeft || !preferredHingeRight);
        partner = partnerOnRight ? rightCandidate : leftCandidate;
        return true;
    }

    private bool TryFindPairedDoor(Vector3i bottom, byte state, out Vector3i partner)
    {
        int facing = PieceMask.DoorFacing(state);
        Vector3i right = PieceMask.DoorRightStep(facing);
        partner = bottom + (PieceMask.DoorHingeRight(state) ? -right : right);
        if (!IsCompleteDoorFacing(partner, facing))
        {
            return false;
        }

        byte partnerState = _world!.GetPieces(partner.X, partner.Y, partner.Z);
        return PieceMask.DoorHingeRight(partnerState) != PieceMask.DoorHingeRight(state);
    }

    private bool IsCompleteDoorFacing(Vector3i bottom, int facing)
    {
        if (_world is null)
        {
            return false;
        }

        string lowerId = _world.Registry.Definition(
            _world.GetBlock(bottom.X, bottom.Y, bottom.Z)).Id;
        string upperId = _world.Registry.Definition(
            _world.GetBlock(bottom.X, bottom.Y + 1, bottom.Z)).Id;
        if (lowerId != "tesseris:wooden_door" || upperId != "tesseris:wooden_door_top")
        {
            return false;
        }

        return PieceMask.DoorFacing(_world.GetPieces(bottom.X, bottom.Y, bottom.Z)) == facing;
    }

    private void ApplyDoorState(Vector3i bottom, byte state)
    {
        _world!.SetPieces(bottom.X, bottom.Y, bottom.Z, state);
        _world.SetPieces(bottom.X, bottom.Y + 1, bottom.Z, state);
        _streamer!.InvalidateInteractiveBlock(bottom.X, bottom.Y, bottom.Z);
        _streamer.InvalidateInteractiveBlock(bottom.X, bottom.Y + 1, bottom.Z);
    }

    private void RemoveDoorMate(Vector3i block, string id)
    {
        if (_world is null || _streamer is null) return;
        Vector3i animationBlock = id == "tesseris:wooden_door_top"
            ? block - Vector3i.UnitY
            : block;
        _buildingPartAnimations.Remove(animationBlock);

        Vector3i mate = id switch
        {
            "tesseris:wooden_door" => block + Vector3i.UnitY,
            "tesseris:wooden_door_top" => block - Vector3i.UnitY,
            _ => block,
        };
        if (mate == block) return;
        string expected = id.EndsWith("_top", StringComparison.Ordinal)
            ? "tesseris:wooden_door" : "tesseris:wooden_door_top";
        ushort mateBlock = _world.GetBlock(mate.X, mate.Y, mate.Z);
        if (_world.Registry.Definition(mateBlock).Id != expected) return;
        _world.SetBlock(mate.X, mate.Y, mate.Z, BlockRegistry.Air);
        _streamer.InvalidateInteractiveBlock(mate.X, mate.Y, mate.Z);
    }

    private bool DispatchModUse(
        Tesseris.ModApi.ModUseKind use,
        Tesseris.ModApi.ModInputPhase phase)
    {
        if (_mods is null || _world is null || _registry is null)
        {
            return false;
        }

        bool hasHit = MicroRaycast.Cast(
            _world,
            _player.EyePosition,
            _camera.Forward,
            ReachDistance,
            out MicroHit hit,
            hitLiquid: HoldingBucket());
        Tesseris.ModApi.ModHit? modHit = hasHit ? ToModHit(hit) : null;
        Tesseris.ModApi.ResourceId? held = HeldModItemId();
        Tesseris.ModApi.ModInventoryStackSnapshot? heldStack = null;
        if (_inventory is not null)
        {
            _modGame.TryGetSlot(_inventory.Selected, out heldStack);
        }

        if (held is { } item)
        {
            Tesseris.ModApi.ModActionResult itemResult = _mods.Behaviors.DispatchItemUse(
                item,
                use,
                phase,
                modHit,
                _modTick,
                heldStack);
            if (itemResult != Tesseris.ModApi.ModActionResult.Pass)
            {
                return true;
            }
        }

        if (!hasHit)
        {
            return false;
        }

        ushort block = _world.GetBlock(hit.Block.X, hit.Block.Y, hit.Block.Z);
        if (block == BlockRegistry.Air || block >= _registry.Count)
        {
            return false;
        }

        Tesseris.ModApi.ModActionResult blockResult = _mods.Behaviors.DispatchBlockInteraction(
            new Tesseris.ModApi.ResourceId(_registry.Definition(block).Id),
            new Tesseris.ModApi.ModBlockPosition(hit.Block.X, hit.Block.Y, hit.Block.Z),
            held,
            use,
            phase,
            modHit!.Value,
            _modTick,
            heldStack);
        return blockResult != Tesseris.ModApi.ModActionResult.Pass;
    }

    private static Tesseris.ModApi.ModHit ToModHit(MicroHit hit) => new(
        new Tesseris.ModApi.ModBlockPosition(hit.Block.X, hit.Block.Y, hit.Block.Z),
        new Tesseris.ModApi.ModVector3(hit.Position.X, hit.Position.Y, hit.Position.Z),
        new Tesseris.ModApi.ModInt3(hit.Normal.X, hit.Normal.Y, hit.Normal.Z));

    /// <summary>
    /// Sesype rostliny, které nad zadaným blokem ztratily podklad.
    ///
    /// <para>Zneplatní se i chunky těch rostlin. Sloupec chaluhy sahá přes několik bloků
    /// a může přesáhnout do chunku nad, takže nestačí přemeshovat ten, ve kterém se
    /// kopalo.</para>
    /// </summary>
    private void DropPlantsAbove(Vector3i block)
    {
        if (_world is null || _streamer is null)
        {
            return;
        }

        int removed = _world.DropUnsupportedPlants(
            block.X,
            block.Y,
            block.Z,
            (at, plant) =>
            {
                // Sesypaná rostlina se opravdu rozbila. V přežití proto zanechá tentýž
                // předmět jako při ručním vytěžení; kreativ zůstává bez dropů.
                if (!_creative && _items is not null && _drops is not null)
                {
                    SpawnDrop(plant, at);
                }
            });

        for (int i = 1; i <= removed; i++)
        {
            _streamer.InvalidateInteractiveBlock(block.X, block.Y + i, block.Z);
        }

        DropUnsupportedTorchesAround(block);
    }

    private void DropUnsupportedTorchesAround(Vector3i changed)
    {
        if (_world is null || _streamer is null)
        {
            return;
        }

        ReadOnlySpan<Vector3i> neighbours =
        [
            Vector3i.UnitX, -Vector3i.UnitX,
            Vector3i.UnitY, -Vector3i.UnitY,
            Vector3i.UnitZ, -Vector3i.UnitZ,
        ];
        foreach (Vector3i step in neighbours)
        {
            Vector3i torch = changed + step;
            ushort block = _world.GetBlock(torch.X, torch.Y, torch.Z);
            if (_world.Registry.ShapeOf(block) != BlockShape.Torch)
            {
                continue;
            }

            byte state = _world.GetPieces(torch.X, torch.Y, torch.Z);
            Vector3i support = torch + PieceMask.TorchSupportDirection(state);
            if (support != changed || _world.Registry.IsSolid(
                    _world.GetBlock(support.X, support.Y, support.Z)))
            {
                continue;
            }

            _world.SetBlock(torch.X, torch.Y, torch.Z, BlockRegistry.Air);
            _world.SetMicro(torch.X, torch.Y, torch.Z, null);
            if (!_creative && _items is not null && _drops is not null)
            {
                SpawnDrop(block, torch);
            }
            _streamer.InvalidateInteractiveBlock(torch.X, torch.Y, torch.Z);
            DispatchRuntimeBlockChange(torch, block, BlockRegistry.Air, HeldModItemId());
        }
    }

    /// <summary>Použije chisel nástroj. Levé tlačítko ubírá, pravé přidává.</summary>
    /// <summary>
    /// Kbelík: nabere vodu ze světa, nebo ji vylije.
    /// </summary>
    /// <remarks>
    /// <para>Levý klik nabírá, pravý vylévá — stejné rozdělení jako u těžby a stavby, takže
    /// si to hráč nemusí pamatovat zvlášť.</para>
    ///
    /// <para>Nabírá se z bloku, na který hráč míří. Vylévá se do bloku PŘED ním, tedy tam,
    /// kam by se položil blok — do vody samotné by se vylévat nedalo a hráč by nevěděl,
    /// proč se nic neděje.</para>
    /// </remarks>
    private void UseBucket(in MicroHit hit, bool fillPressed)
    {
        if (_world is null || _streamer is null || _hotbar is null)
        {
            return;
        }

        if (fillPressed)
        {
            if (_hotbar.BucketFull)
            {
                return;
            }

            if (!_world.Registry.IsLiquid(_world.GetBlock(hit.Block.X, hit.Block.Y, hit.Block.Z)))
            {
                return;
            }

            // NABÍRÁ SE JEN Z PLNÉHO BLOKU. Doběh je jen stopa, kterou po sobě voda nechala
            // cestou — nabrat z ní celý kbelík by znamenalo vyrobit vodu z ničeho. Zdroj je
            // naopak plný blok a po nabrání zmizí celý.
            if (_world.GetFluid(hit.Block.X, hit.Block.Y, hit.Block.Z) < FluidCell.Source)
            {
                return;
            }

            _world.SetBlock(hit.Block.X, hit.Block.Y, hit.Block.Z, BlockRegistry.Air);
            _streamer.InvalidateInteractiveBlock(hit.Block.X, hit.Block.Y, hit.Block.Z);
            _fluid?.Touch(hit.Block.X, hit.Block.Y, hit.Block.Z);
            _hotbar.FillBucket();
            return;
        }

        if (!_hotbar.BucketFull)
        {
            return;
        }

        ushort water = _world.Registry.IndexOf("tesseris:water");

        // MÍŘÍM-LI NA VODU, VYLIJE SE DO NÍ. Jinak by blok skončil NAD hladinou a z mělčiny
        // by vznikl sloupec o patro vyšší, místo aby se srovnala s okolím.
        //
        // Platí to i pro plnou vodu: tam se sice nic nezmění, ale kbelík se vyprázdní.
        // Klikat do jezera a nedočkat se ničeho, ani prázdného kbelíku, vypadá rozbitě.
        bool intoWater = _world.GetBlock(hit.Block.X, hit.Block.Y, hit.Block.Z) == water;

        Vector3i spill = intoWater ? hit.Block : hit.Block + hit.Normal;

        if (_player.Overlaps(spill) || spill.Y < 0 || spill.Y >= TerrainGenerator.WorldHeight)
        {
            return;
        }

        ushort standing = _world.GetBlock(spill.X, spill.Y, spill.Z);

        // Do pevného bloku se voda nevejde.
        if (standing != BlockRegistry.Air && standing != water)
        {
            return;
        }

        // DO PLNÉ VODY SE VYLÉVAT DÁ, jen se tím nic nezmění — voda splyne s tou, co tam
        // je, a kbelík zůstane prázdný. Zakazovat to bylo horší: hráč klikal do jezera
        // a nedělo se vůbec nic, ani kbelík se nevyprázdnil, takže to vypadalo rozbitě.
        _world.PlaceFluidSource(spill.X, spill.Y, spill.Z, water);
        _streamer.InvalidateInteractiveBlock(spill.X, spill.Y, spill.Z);
        _fluid?.Touch(spill.X, spill.Y, spill.Z);
        _hotbar.EmptyBucket();
    }

    private void ApplyChisel(in MicroHit hit, bool removing)
    {
        if (_world is null || _streamer is null || _hotbar is null)
        {
            return;
        }

        ChiselMode requested = _chisel.Mode;

        // Levé a pravé tlačítko přepínají mezi ubráním a přidáním; ostatní režimy
        // se vybírají klávesou a tlačítko je jen spustí.
        if (requested is ChiselMode.Remove or ChiselMode.Add)
        {
            _chisel.Mode = removing ? ChiselMode.Remove : ChiselMode.Add;
        }

        _chisel.Material = _hotbar.SelectedBlock;

        if (_chisel.Apply(_world, hit, out Vector3i affected))
        {
            // Tesáním může blok zmizet celý, takže i tudy se dá rostlině vzít podklad.
            DropPlantsAbove(affected);

            _streamer.InvalidateInteractiveBlock(affected.X, affected.Y, affected.Z);

            // Zvuk zní z bloku, do kterého se seklo — ne ze středu hlavy.
            // Když se blok tesáním celý ztratil, materiál se vezme z toho, co paprsek trefil.
            BlockMaterial material = TryGetMaterial(affected, out BlockMaterial found)
                ? found
                : _world.Registry.Definition(hit.Material).Material;

            _sounds?.Play(material, BlockAction.Chisel, affected);
        }

        _chisel.Mode = requested;
    }
}
