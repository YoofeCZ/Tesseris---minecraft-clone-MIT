using System.Reflection;
using System.Runtime.InteropServices;
using OpenTK.Mathematics;
using Tesseris.Engine.MathLib;
using Tesseris.Engine.Rendering;
using Silk.NET.Vulkan;
using Tesseris.Engine.Rendering.Vulkan;
using Tesseris.Game.Blocks;
using Tesseris.Game.Entities;
using Tesseris.Game.Items;

namespace Tesseris.Game.World;

/// <summary>
/// Vykreslování chunků.
///
/// <para>
/// Každý chunk má oddělené buffery pro neprůhlednou, průhlednou a mikro geometrii.
/// Formát vrcholů je pro všechny stejný, takže se před průchodem jen naváže jiný buffer.
/// </para>
///
/// <para>
/// <b>Co se změnilo proti OpenGL verzi.</b> Tam existoval jeden shader a průchody se lišily
/// voláními <c>glEnable(GL_BLEND)</c> a <c>glDepthMask</c> za běhu. Vulkan chce celý stav
/// předem zapečený v pipeline, takže z těch přepínačů jsou samostatné objekty. Uniformy
/// nahradily push konstanty a VAO zmizelo — popis vrcholu je taky součástí pipeline.
/// </para>
/// </summary>
public sealed unsafe class ChunkRenderer : IDisposable
{
    private const int Stride = MeshBuffer.FloatsPerVertex * sizeof(float);

    private const uint PushConstantSize =
        (16 * sizeof(float)) + (4 * 4 * sizeof(float)) + (2 * 3 * 4 * sizeof(float)) + (2 * 4 * sizeof(float));

    /// <summary>
    /// Kde v bloku push konstant začíná posun chunku. Mění se pro každý draw call, proto
    /// leží až na konci — zbytek se posílá jednou na průchod.
    /// </summary>
    private const uint ChunkOffsetOffset = (16 * sizeof(float)) + (3 * 4 * sizeof(float));

    /// <summary>
    /// mat4 + čtyři vec4 = 128 bajtů, tedy stejný strop jako u terénu. Obloha má vlastní
    /// blok push konstant, protože má vlastní pipeline — Vulkan váže blok na pipeline,
    /// ne globálně.
    /// </summary>
    private const uint BloomPushConstantSize = 4 * sizeof(float);

    /// <summary>Matice slunce plus posun chunku. Vejde se i do zaručených 128 B.</summary>
    private const uint ShadowPushConstantSize = (16 * sizeof(float)) + (4 * sizeof(float));

    private const uint ShadowOffsetOffset = 16 * sizeof(float);

    /// <summary>Index prvního floatu světelné části push konstant (matice slunce).</summary>
    private const int LightConstantOffset = 32;

    /// <summary>
    /// Kolik floatů zabírá světelná část: dvě matice po třech vec4 a dva vec4 ladění.
    /// </summary>
    private const int LightConstantSize = (2 * 3 * 4) + (2 * 4);

    /// <summary>Kolik floatů musí pole mít, aby se světelná část dala poslat.</summary>
    private const int LightConstantFloats = LightConstantOffset + LightConstantSize;

    /// <summary>
    /// Obloha: tři osy paprsku, kamera s časem, slunce, nadhlavník a obzor.
    /// </summary>
    /// <remarks>
    /// Bylo to o mat4 víc, dokud se směr paprsku počítal z inverzní matice pohledu.
    /// Tři vektory jsou nejen přesnější, ale i menší — viz <see cref="SkyRay"/>.
    /// </remarks>
    private const int SkyConstantFloats = 7 * 4;

    private const uint SkyPushConstantSize = SkyConstantFloats * sizeof(float);

    private const uint ShadowTextureCount = 5;

    private readonly VulkanContext _context;
    private readonly VulkanSwapchain _swapchain;
    private readonly VulkanRenderer _renderer;
    private readonly TextureArray _textures;

    /// <summary>
    /// Dvě hotové sady vazeb, mezi kterými se na hranici epochy jen přepíná.
    /// </summary>
    /// <remarks>
    /// každou epochu prohodí — jednou je čerstvá první a druhá nese předchozí epochu,
    /// příště naopak. Přepsat kvůli tomu vazby v jedné sadě by znamenalo sahat na
    /// deskriptor, ze kterého možná zrovna čte snímek ve frontě.</para>
    ///
    /// <para>Obě sady se proto zapíšou jednou při startu a liší se jen pořadím: první má
    /// na vazbách 1 a 2 mapy A a na 3 a 4 mapy B, druhá obráceně. Přepnutí epochy je tím
    /// jediné přiřazení ukazatele.</para>
    /// </remarks>
    private readonly VulkanTextureSet _descriptorA;

    /// <summary>
    /// Sada pro vodu: atlas bloků, kopie scény a hloubka.
    ///
    /// <para>Zvlášť od <see cref="_descriptorA"/>, protože poslední dvě vazby ukazují na
    /// obrazy velké jako okno — po každé změně velikosti se musí přepsat.</para>
    /// </summary>
    private readonly VulkanTextureSet _waterDescriptor;

    /// <summary>Je aktuální epocha v dvojici B? Na hranici epochy se překlápí.</summary>

    /// <summary>Kolik bajtů drží zásoba odložených chunků.</summary>
    private long _pooledBytes;

    /// <summary>Kolik bajtů drží zásoba odložených dlaždic vzdáleného terénu.</summary>
    private long _pooledFarBytes;
    private readonly VulkanPipeline _colorGrade;
    private VulkanTextureSet _colorGradeDescriptor = null!;
    private readonly VulkanPipeline _bloom;
    private VulkanTextureSet _bloomDescriptor = null!;

    /// <summary>
    /// Instancované kreslení itemů. Jediná cesta v projektu, která používá druhou vertex
    /// vazbu a <c>instanceCount</c> větší než jedna.
    /// </summary>
    private readonly InstancedItemRenderer _instancedItems;
    private readonly VulkanTextureSet _instancedItemsDescriptor;

    private readonly VulkanPipeline _opaque;
    private readonly VulkanPipeline _transparent;
    private readonly VulkanPipeline _opaqueNoCull;
    private readonly VulkanPipeline _transparentNoCull;

    /// <summary>Pipeline vzdáleného terénu. Liší se jen shaderem s blízkým řezem.</summary>
    private readonly VulkanPipeline _farPipeline;

    /// <summary>
    /// Pipeline rostlin ve vzdáleném terénu: alfa test se zápisem do hloubky.
    ///
    /// <para>Vlastní shader, protože potřebuje obojí najednou — blízký řez jako
    /// <c>far.frag</c> a alfa test jako výřezový průchod chunků. Ani jeden ze stávajících
    /// shaderů to neumí.</para>
    /// </summary>
    private readonly VulkanPipeline _farPlants;

    /// <summary>Pipeline rostlin: alfa test se zápisem do hloubky, bez míchání.</summary>
    private readonly VulkanPipeline _cutout;

    /// <summary>Pipeline ležících předmětů. Výřez bez větru.</summary>
    private readonly VulkanPipeline _items;

    /// <summary>Pipeline vody. Vlastní shader s Fresnelem a odleskem, bez cullingu.</summary>
    private readonly VulkanPipeline _water;

    /// <summary>Pipeline hladiny vzdáleného terénu. Týž shader jako voda, ale neprůhledně.</summary>
    private readonly VulkanPipeline _farWater;

    /// <summary>Pipeline oblohy. Fullscreen trojúhelník bez vertex bufferu.</summary>
    private readonly VulkanPipeline _sky;

    private readonly VulkanTextureSet _skyDescriptor;

    private readonly VulkanPipeline _cloudOverlay;

    private readonly VulkanTextureSet _cloudOverlayDescriptor;

    /// <summary>Low spatial fog integrated between the camera and scene depth.</summary>
    private readonly VulkanPipeline _volumetricFog;

    /// <summary>
    /// Měření času grafiky po průchodech. Odpovídá na otázku, který průchod stojí kolik —
    /// což z času hlavního vlákna zjistit nejde.
    /// </summary>
    private readonly VulkanGpuProfiler _gpu;

    /// <summary>
    /// Uniformní blok, do kterého jdou konstanty snímku a posun chunku.
    ///
    /// <para>Jeden na celý renderer, ne na průchod: v OpenGL se váže na bod, ne na program,
    /// takže ho všechny průchody čtou ze stejného místa.</para>
    /// </summary>
    private readonly Dictionary<Vector3i, GpuChunk> _chunks = [];

    // Dlaždice vzdáleného terénu. Drží se zvlášť od chunků, protože mají jinou životnost
    // i jiný klíč — a hlavně se kreslí bez posunu, souřadnice mají rovnou světové.
    private readonly Dictionary<FarTerrain.TileKey, FarTile> _far = [];

    /// <summary>Odložené buffery dlaždic. Stejný důvod jako u chunků: GPU z nich může ještě číst.</summary>
    private readonly Queue<(GpuBuffers Buffers, long Frame)> _retiringFar = new();

    /// <summary>
    /// Volné buffery dlaždic k dalšímu použití.
    ///
    /// <para>Bez zásoby se pro každou dlaždici zakládaly nové buffery. Při třech pásmech
    /// LOD se dlaždice mění tak často, že to naměřeno stálo <b>157 ms v jednom snímku</b>
    /// — alokace paměti zařízení je ve Vulkanu drahá. Chunky zásobu měly od začátku,
    /// dlaždice na ni zapomněly.</para>
    /// </summary>
    private readonly Queue<GpuBuffers> _freeFar = new();

    // Seznam viditelných chunků se drží mezi framy. Zakládat ho pokaždé znovu znamenalo
    // několik kilobajtů odpadu na frame včetně přealokování při růstu.
    private readonly List<Vector3i> _visible = [];

    /// <summary>
    /// Odložené prostředky. Proti OpenGL verzi přibyl u každé položky <b>snímek</b>,
    /// ve kterém byla odložena.
    ///
    /// Ve Vulkanu totiž nestačí buffer jen tak vrátit do zásoby: GPU z něj může ještě
    /// kreslit rozpracovaný snímek. Půjčit se smí, až když je jistota, že všechny snímky,
    /// které ho mohly číst, doběhly — tedy po <c>FramesInFlight</c> snímcích.
    /// </summary>
    private readonly Queue<(GpuChunk Chunk, long Frame)> _retiring = new();

    // FIFO, ne zásobník. Ze zásobníku se recykluje naposledy vrácený chunk — tedy přesně
    // ten, jehož buffery GPU s největší pravděpodobností ještě čte, takže se na ně musí
    // počkat. Frontou se recyklovaný chunk dostane ke slovu až po mnoha dalších a čekání
    // odpadá. Naměřeno: nejhorší nahrání 27,2 ms se zásobníkem.
    private readonly Queue<GpuChunk> _free = new();

    /// <summary>Nejvíc odložených sad bufferů. Nad tím se prostředky opravdu uvolní.</summary>
    // Zvednuto z 512 při přechodu na svět vysoký 1024 bloků. Obrat bufferů je od té doby
    // několikanásobný a s malým poolem se pořád zakládaly nové — jedno nahrání pak stálo
    // 21 ms, protože ovladač na alokaci čeká.
    /// <summary>
    /// Kolik odložených chunků se drží v zásobě pro další použití.
    /// </summary>
    /// <remarks>
    /// <para><b>Bylo tu 1024 a byla to hlavní příčina toho, že VRAM při chůzi jen rostla.</b>
    /// Odložení chunku totiž jeho buffery neuvolní — <c>Retire</c> jen vynuluje počet indexů
    /// a paměť na GPU zůstane. Prvních tisíc odstraněných chunků tedy nevrátilo ani bajt.
    /// Při naměřených ~450 kB na chunk držela samotná zásoba kolem 450 MB, a totéž číslo
    /// platilo zvlášť pro dlaždice vzdáleného terénu, které jsou ještě větší.</para>
    ///
    /// <para>Sto dvacet osm stačí: recyklují se chunky, které se odloží a hned zase nahrají,
    /// což je pár desítek na překročení hranice. Co je nad to, byla mrtvá paměť.</para>
    /// </remarks>
    private const int MaxPooledChunks = 128;

    /// <summary>
    /// Strop zásoby v bajtech. Pojistka pro případ, že by se do zásoby dostaly samé
    /// obří chunky — počet sám o velikosti nic neříká.
    /// </summary>
    private const long MaxPooledBytes = 128L * 1024 * 1024;

    /// <summary>
    /// Kolik dlaždic vzdáleného terénu se drží v zásobě.
    /// </summary>
    /// <remarks>
    /// Míň než chunků: jedna dlaždice pokrývá 64×64 buněk, tedy kus světa velký jako
    /// spousta chunků, a její mesh je tomu úměrný. Dřív tu platil týž strop 1024 jako
    /// u chunků a tahle jediná zásoba držela řádově stovky megabajtů VRAM.
    /// </remarks>
    private const int MaxPooledTiles = 24;

    public ChunkRenderer(
        VulkanContext context, VulkanRenderer renderer, VulkanSwapchain swapchain,
        TextureArray textures, int shadowMapSize = 2048)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        _swapchain = swapchain ?? throw new ArgumentNullException(nameof(swapchain));
        _textures = textures ?? throw new ArgumentNullException(nameof(textures));

        // Formát vrcholu: pozice, dlaždicovací UV, vrstva textury, stínění rohu, světlo.
        VertexInputAttributeDescription[] attributes =
        [
            new() { Location = 0, Binding = 0, Format = Format.R32G32B32Sfloat, Offset = 0 },
            new() { Location = 1, Binding = 0, Format = Format.R32G32Sfloat, Offset = 3 * sizeof(float) },
            new() { Location = 2, Binding = 0, Format = Format.R32Sfloat, Offset = 5 * sizeof(float) },
            new() { Location = 3, Binding = 0, Format = Format.R32Sfloat, Offset = 6 * sizeof(float) },
            new() { Location = 4, Binding = 0, Format = Format.R32Sfloat, Offset = 7 * sizeof(float) },
        ];

        Assembly assembly = Assembly.GetExecutingAssembly();

        // Neprůhledný průchod má vlastní shader BEZ discardu.
        //
        // Fragment shader s discardem grafice zakazuje early-Z: dokud shader nedoběhne,
        // terénem a teprve pak zahodí. Ve voxelovém světě, kde se geometrie hodně
        // překrývá, je to drahé přesně tam, kde je scéna nejhustší. Neprůhledné bloky
        // mají alfu 1, takže tam discard nikdy nezabral — byl to mrtvý kód s velkou cenou.
        // PĚT VAZEB VŠUDE, I KDYŽ SHADER ČTE JEN PRVNÍ.
        //
        // Všechny tyhle pipeline dostávají tutéž sadu deskriptorů, a Vulkan chce, aby set
        // odpovídal layoutu pipeline, do které se váže. Kdo z map nečte, prostě vazby
        // nedeklaruje — prázdná vazba v layoutu nic nestojí.
        VulkanPipeline Build(
            string fragment, BlendMode mode, CullModeFlags cull,
            string vertex = "chunk.vert.spv", uint textures = ShadowTextureCount) =>
            VulkanPipeline.Create(
                context, swapchain, assembly, vertex, fragment,
                Stride, attributes, PushConstantSize, mode, cull, textureCount: textures,
                colorFormat: VulkanSwapchain.SceneColorFormat);

        // na 1 a 2, předchozí na 3 a 4.
        _opaque = Build("chunk_opaque.frag.spv", BlendMode.Opaque, CullModeFlags.BackBit);
        _transparent = Build("chunk.frag.spv", BlendMode.AlphaBlend, CullModeFlags.BackBit);

        // Varianty bez cullingu existují jen kvůli selftestu, který porovnává scénu
        // s cullingem a bez něj. V OpenGL na to stačilo glDisable(GL_CULL_FACE),
        // ve Vulkanu je culling součástí pipeline, takže musí být druhý objekt.
        _opaqueNoCull = Build("chunk_opaque.frag.spv", BlendMode.Opaque, CullModeFlags.None);
        _transparentNoCull = Build("chunk.frag.spv", BlendMode.AlphaBlend, CullModeFlags.None);

        _farPipeline = Build("far.frag.spv", BlendMode.Opaque, CullModeFlags.BackBit);

        // Rostliny v LOD: výřez se zápisem do hloubky, jako tráva v chuncích. Culling
        // zůstává zapnutý — mesh nese každou plochu dvakrát s opačným navíjením, takže
        // je vidět z obou stran i tak.
        _farPlants = Build("far_plants.frag.spv", BlendMode.Cutout, CullModeFlags.BackBit);

        // Rostliny: shader s alfa testem (chunk.frag), ale se zápisem do hloubky a bez
        // míchání. Díky zápisu do hloubky se stébla navzájem zakryjí a v louce se
        // VÝŘEZOVÝ PRŮCHOD JEDE PŘES VĚTRNÝ VERTEX SHADER.
        //
        // Do výřezu padá porost - tráva, květiny, listí - a jenom ten se smí hýbat.
        // Pevné bloky mají vlastní pipeline se statickým shaderem: kdyby se rozhýbaly
        // i ony, rozjely by se stěny sousedních chunků a mezi nimi by byly spáry.
        _cutout = Build(
            "chunk.frag.spv", BlendMode.Cutout, CullModeFlags.BackBit, "chunk_wind.vert.spv");

        // LEŽÍCÍ PŘEDMĚTY. Výřez jako porost, ale BEZ větru: krychlička s předmětem se
        // hýbat nemá, a větrný vertex shader by ji navíc ohýbal podle texturové souřadnice,
        // což u krychle nedává smysl vůbec.
        //
        // A BEZ CULLINGU. Se zapnutým odstraněním odvrácených stěn zmizela půlka krychličky
        // a z předmětu na zemi byl ohnutý plát — navíjení jejích stěn nesedělo na to, co
        // pipeline čeká. U pěti čtyřúhelníků na předmět je levnější kreslit obě strany než
        // se spoléhat na to, že je pořadí vrcholů všude správně.
        _items = Build("chunk.frag.spv", BlendMode.Cutout, CullModeFlags.None);

        // Voda: vlastní shader, jinak týž stav jako sklo.
        //
        // Culling je VYPNUTÝ schválně. Hladina je jediná plocha mířící nahoru a potápěč
        // se na ni dívá zespodu — se zapnutým cullingem by nad sebou viděl díru do nebe.
        //
        // Tři textury místo jedné: k atlasu ještě kopie scény a hloubka, ze kterých se
        // počítá screen-space odraz a lom.
        _water = VulkanPipeline.Create(
            context, swapchain, assembly, "water.vert.spv", "water.frag.spv",
            Stride, attributes, PushConstantSize, BlendMode.AlphaBlend, CullModeFlags.None,
            textureCount: 3, colorFormat: VulkanSwapchain.SceneColorFormat);

        // Hladina vzdáleného terénu: TÝŽ shader. Pod ní žádná geometrie není — dno se
        // u LOD zvedne na hladinu — takže shader ji kreslí s alfou 1 a nic skrz ni
        // neprosvítá.
        //
        // <b>Míchání místo neprůhledného stavu.</b> Dřív to byl BlendMode.Opaque, jenže ten
        // zapisuje do hloubky, a vzdálená hladina se nově kreslí až za rozdělením snímku,
        // kde je hloubka připojená jen ke čtení. Výsledek je týž: shader v téhle větvi
        // vrací alfu 1, takže SrcAlpha/OneMinusSrcAlpha dá přesně zdrojovou barvu.
        _farWater = VulkanPipeline.Create(
            context, swapchain, assembly, "water.vert.spv", "water.frag.spv",
            Stride, attributes, PushConstantSize, BlendMode.AlphaBlend, CullModeFlags.None,
            textureCount: 3, colorFormat: VulkanSwapchain.SceneColorFormat);

        // Obloha: fullscreen trojúhelník bez vertex bufferu, kreslený jako PRVNÍ.
        // Overlay znamená bez hloubkového testu i zápisu, takže vyplní obraz a terén se
        // pak normálně vykreslí přes něj.
        _sky = VulkanPipeline.Create(
            context, swapchain, assembly, "sky.vert.spv", "sky.frag.spv",
            0, [], SkyPushConstantSize, BlendMode.Overlay, CullModeFlags.None,
            colorFormat: VulkanSwapchain.SceneColorFormat);

        _cloudOverlay = VulkanPipeline.Create(
            context, swapchain, assembly, "sky.vert.spv", "cloud_overlay.frag.spv",
            0, [], SkyPushConstantSize, BlendMode.AlphaBlend, CullModeFlags.None,
            colorFormat: VulkanSwapchain.SceneColorFormat);

        // Fullscreen fog uses the water descriptor because that set already exposes the
        // resolved scene depth in DepthReadOnlyOptimal after CaptureSceneForWater.
        _volumetricFog = VulkanPipeline.Create(
            context, swapchain, assembly, "sky.vert.spv", "volumetric_fog.frag.spv",
            0, [], SkyPushConstantSize, BlendMode.AlphaBlend, CullModeFlags.None,
            textureCount: 3, colorFormat: VulkanSwapchain.SceneColorFormat);

        // BLOOM. Fullscreen trojúhelník bez vertex bufferu, stejně jako obloha; čte kopii
        // scény a aditivně k ní přičte rozmazaná světlá místa.
        _colorGrade = VulkanPipeline.Create(
            context, swapchain, assembly, "bloom.vert.spv", "color_grade.frag.spv",
            0, [], BloomPushConstantSize, BlendMode.Overlay, CullModeFlags.None,
            rasterizationSamples: SampleCountFlags.Count1Bit);

        _bloom = VulkanPipeline.Create(
            context, swapchain, assembly, "bloom.vert.spv", "bloom.frag.spv",
            0, [], BloomPushConstantSize, BlendMode.Add, CullModeFlags.None,

            // Bloom se kreslí ještě do scény, tedy do vícevzorkového HDR obrazu —
            // volá se před BeginPresentation.
            colorFormat: VulkanSwapchain.SceneColorFormat);

        // Stejný formát i počet vzorků jako neprůhledný průchod — itemy se kreslí do scény,
        // ne až na obrazovku, takže se s cílem musí shodovat, jinak ovladač odmítne snímek.
        _instancedItems = new InstancedItemRenderer(
            context,
            renderer,
            swapchain,
            typeof(ChunkRenderer).Assembly,
            PushConstantSize,
            colorFormat: VulkanSwapchain.SceneColorFormat);

        _instancedItemsDescriptor = new VulkanTextureSet(
            context, _instancedItems.DescriptorSetLayout, BlockAtlas());

        _descriptorA = new VulkanTextureSet(context, _opaque.DescriptorSetLayout, BlockAtlas());
        _skyDescriptor = new VulkanTextureSet(context, _sky.DescriptorSetLayout, BlockAtlas());
        _cloudOverlayDescriptor = new VulkanTextureSet(context, _cloudOverlay.DescriptorSetLayout, BlockAtlas());

        _waterDescriptor = new VulkanTextureSet(context, _water.DescriptorSetLayout, WaterEntries());
        _colorGradeDescriptor = new VulkanTextureSet(context, _colorGrade.DescriptorSetLayout, HdrScene());
        _bloomDescriptor = new VulkanTextureSet(context, _bloom.DescriptorSetLayout, SceneCopy());

        // Pořadí musí sedět na pořadí značek v Draw.
        _gpu = new VulkanGpuProfiler(
            context,
            "obloha",
            "chunky",
            "LOD teren",
            "LOD rostliny",
            "mikro",
            "vyrez",
            "pruhledne",
            "kopie sceny",
            "voda",
            "LOD voda");

        // Renderer sadu značek nuluje na začátku snímku — uvnitř renderingu to nejde.
        renderer.GpuProfiler = _gpu;
    }

    /// <summary>Měření času grafiky po průchodech. Pro overlay.</summary>
    public VulkanGpuProfiler GpuProfiler => _gpu;

    private VulkanTextureSet.Entry[] BlockAtlas() =>
        [VulkanTextureSet.Entry.From(_textures.Texture)];

    private VulkanTextureSet.Entry[] HdrScene() =>
        [new(_swapchain.SceneSampler, _swapchain.SceneView)];

    private VulkanTextureSet.Entry[] SceneCopy() =>
        [new(_swapchain.SceneSampler, _swapchain.SceneView)];

    /// <summary>Vazby vodní sady v pořadí, v jakém je čeká <c>water.frag</c>.</summary>
    private VulkanTextureSet.Entry[] WaterEntries() =>
    [
        VulkanTextureSet.Entry.From(_textures.Texture),
        new(_swapchain.SceneSampler, _swapchain.SceneView),

        // Hloubka je ve chvíli čtení pořád připojená jako příloha, takže leží
        // v DepthReadOnlyOptimal, ne v ShaderReadOnlyOptimal jako běžná textura.
        new(_swapchain.DepthSampler, _swapchain.DepthSampleView, ImageLayout.DepthReadOnlyOptimal),
    ];

    /// <summary>
    /// Přepíše sady, které ukazují na cíle velké jako okno.
    /// </summary>
    /// <remarks>
    /// Musí se zavolat po každé změně velikosti okna: kopie scény i hloubka jsou tehdy
    /// nové textury a stará sada by vázala už zrušené.
    /// </remarks>
    public void HandleResize()
    {
        _waterDescriptor.Write(WaterEntries());
        _colorGradeDescriptor.Write(HdrScene());
        _bloomDescriptor.Write(SceneCopy());
    }

    /// <summary>Kolik chunků prošlo frustum cullingem v posledním kreslení.</summary>
    public int VisibleChunks { get; private set; }

    /// <summary>Kolik chunků má nahranou geometrii.</summary>
    public int LoadedChunks => _chunks.Count;

    /// <summary>Kolik dlaždic vzdáleného terénu je nahraných.</summary>
    public int FarTiles => _far.Count;

    /// <summary>Kolik dlaždic vzdáleného terénu prošlo cullingem naposledy.</summary>
    public int VisibleFarTiles { get; private set; }

    /// <summary>
    /// Součet trojúhelníků ve všech nahraných chuncích. Počítá se až na vyžádání —
    /// slouží k diagnostice, ne k rozhodování za běhu.
    /// </summary>
    public int TriangleCount
    {
        get
        {
            int total = 0;
            foreach (GpuChunk gpu in _chunks.Values)
            {
                total += (gpu.Opaque.IndexCount
                    + gpu.Transparent.IndexCount
                    + gpu.Cutout.IndexCount
                    + gpu.Micro.IndexCount
                    + gpu.Water.IndexCount) / 3;
            }

            return total;
        }
    }

    /// <summary>
    /// Zapnutý backface culling. Vypíná se jen při ověřování, že stěny mají správné
    /// navíjení — při špatném navíjení se scéna s cullingem a bez něj liší.
    /// </summary>
    public bool BackfaceCulling { get; set; } = true;

    /// <summary>
    /// Barva oblohy. Používá se i jako barva mlhy, aby terén u obzoru splynul s nebem
    /// místo aby končil ostrým okrajem tam, kde dohled přestává.
    /// </summary>
    public static readonly Vector3 SkyColor = new(0.62f, 0.76f, 0.92f);

    /// <summary>
    /// Barva oblohy v nadhlavníku.
    ///
    /// <para>Sytější a tmavší než u obzoru schválně. Vzduch rozptyluje krátké vlnové délky
    /// silněji, takže vzhůru se kouká skrz tenkou vrstvu a je vidět víc modré, kdežto
    /// k obzoru vede paprsek mnohem delší dráhou a barva vybledne do bíla. Právě ten
    /// přechod dělá z nebe nebe — plochá barva vypadá jako namalované pozadí.</para>
    /// </summary>
    public static readonly Vector3 ZenithColor = new(0.20f, 0.42f, 0.82f);

    /// <summary>
    /// Barva mlhy pod hladinou. Tmavší a modřejší než obloha, aby bylo poznat, že je
    /// hráč pod vodou, i kdyby zrovna koukal do prázdna.
    /// </summary>
    public static readonly Vector3 UnderwaterColor = new(0.06f, 0.20f, 0.38f);

    /// <summary>
    /// Barva mlhy. Normálně obloha, pod vodou modrá tma.
    /// </summary>
    /// <remarks>
    /// Musí se propsat i do barvy, kterou se maže obraz — jinak by tam, kam nedosáhne
    /// žádná geometrie, prosvítalo nebe a hráč by pod vodou koukal na modré okno do světa.
    /// </remarks>
    public Vector3 FogColor { get; set; } = SkyColor;

    /// <summary>Mořská hladina ve světových souřadnicích. Podle ní shader počítá pohlcení vodou.</summary>
    public float SeaLevel { get; set; }

    /// <summary>
    /// Čas běhu v sekundách. Jediné, co se mění za běhu a co shader vody potřebuje —
    /// podle něj se posouvají vlnky.
    /// </summary>
    /// <remarks>
    /// Posílá se do dosud nevyužité složky <c>water.w</c>, takže blok push konstant
    /// zůstal na 128 bajtech a nebylo potřeba sahat na uniform buffer.
    ///
    /// <para>Ve <b>float</b> to vydrží řádově hodinu, než začne být krok příliš hrubý na
    /// plynulé vlnění. Proto se zabaluje modulem, ne že by rostl donekonečna.</para>
    /// </remarks>
    public float Time { get; set; }

    /// <summary>
    /// Denní doba. Řídí směr slunce, barvu oblohy i barvu světla.
    /// </summary>
    /// <remarks>
    /// Renderer si ji nedrží sám ani neposouvá — dostane ji zvenčí, aby stejný čas viděla
    /// i simulace a cokoli dalšího, co na denní době závisí.
    /// </remarks>
    public DayCycle Day { get; set; } = new();

    /// <summary>
    /// Osy, ze kterých si obloha skládá paprsek. Nastavuje je volající z kamery.
    /// </summary>
    /// <remarks>
    /// <para><b>Nahradily inverzní matici pohledu a je to oprava, ne úklid.</b> Obloha
    /// nemá geometrii, takže si směr paprsku musí dopočítat sama. Dřív to dělala tak, že
    /// bod na vzdálené rovině převedla zpátky do světa inverzí matice, kterou se kreslí
    /// terén. Jenže poměr blízké a vzdálené roviny je 0,12 : 8192 a taková matice je na
    /// inverzi špatně podmíněná.</para>
    ///
    /// <para>Naměřeno ve <c>float</c>, tedy v přesnosti shaderu: paprsek z inverze mířil
    /// vedle až o 0,55 pixelu a při rovnoměrném otáčení kolísal jeho krok o 0,41 px,
    /// zatímco samotný krok byl 0,376 px — <b>kolísání větší než pohyb</b>. Terén se kreslí
    /// maticí přímou, takže stál; poskakovala jen obloha, a na ní je to vidět na slunci.</para>
    ///
    /// <para><c>Right</c> a <c>Up</c> jsou už vynásobené rozevřením pohledu, takže shaderu
    /// stačí <c>Forward + ndc.x * Right + ndc.y * Up</c>. Hlídá to <c>SkyRayTests</c>.</para>
    /// </remarks>
    public (Vector3 Right, Vector3 Up, Vector3 Forward) SkyRay { get; set; }
        = (Vector3.UnitX, Vector3.UnitY, -Vector3.UnitZ);

    private static void WriteVector(Span<float> target, Vector3 value)
    {
        target[0] = value.X;
        target[1] = value.Y;
        target[2] = value.Z;
    }

    private static void WriteMatrix(Span<float> target, Matrix4 matrix)
    {
        target[0] = matrix.M11; target[1] = matrix.M12; target[2] = matrix.M13; target[3] = matrix.M14;
        target[4] = matrix.M21; target[5] = matrix.M22; target[6] = matrix.M23; target[7] = matrix.M24;
        target[8] = matrix.M31; target[9] = matrix.M32; target[10] = matrix.M33; target[11] = matrix.M34;
        target[12] = matrix.M41; target[13] = matrix.M42; target[14] = matrix.M43; target[15] = matrix.M44;
    }

    /// <summary>Předměty ležící ve světě. Nastavuje volající; prázdné znamená, že se nekreslí.</summary>
    public IReadOnlyList<ItemEntity>? Drops { get; set; }

    /// <summary>Registr předmětů kvůli ikonám. Musí sedět na <see cref="Drops"/>.</summary>
    public ItemRegistry? DropItems { get; set; }

    /// <summary>Osvětlení předmětů ležících ve světě v rozsahu 0–1.</summary>
    public Func<Vector3, float>? DropLightSampler { get; set; }

    /// <summary>Prime svetlo oblohy pro dynamicke predmety v rozsahu 0 az 1.</summary>
    public Func<Vector3, float>? SkyLightSampler { get; set; }

    /// <summary>
    /// Blok, do kterého se právě kope, jak daleko to je (0 až 1) a jaký objem opravdu
    /// zabírá — v souřadnicích uvnitř bloku.
    /// </summary>
    /// <remarks>
    /// <b>Rozsah tu je proto, že kmen není celý blok.</b> Sloupek zabírá jen prostřední
    /// polovinu půdorysu, takže praskliny kreslené přes celý blok visely ve vzduchu vedle
    /// kmene místo na něm.
    /// </remarks>
    public (Vector3i Block, float Progress, Aabb Bounds)? Breaking { get; set; }

    /// <summary>Vrstva prvního stupně prasklin. Dalších devět leží za ní.</summary>
    public int CrackLayer { get; set; } = -1;

    private GpuBuffers? _crackBuffers;
    private readonly MeshBuffer _crackMesh = new();

    /// <summary>Kolik bufferů předmětů se střídá. Musí pokrýt všechny rozpracované snímky.</summary>
    private const int ItemBufferSlots = VulkanRenderer.FramesInFlight;

    private readonly GpuBuffers?[] _itemBuffers = new GpuBuffers?[ItemBufferSlots];

    /// <summary>Ruka má vlastní mesh i buffery: kreslí se s jiným rozsahem hloubky.</summary>
    private readonly MeshBuffer _heldMesh = new();
    private readonly GpuBuffers?[] _heldBuffers = new GpuBuffers?[ItemBufferSlots];
    private readonly MeshBuffer _itemMesh = new();

    private readonly MeshBuffer _animalMesh = new();
    private B3dAnimatedModel? _classicSheep;
    private B3dAnimatedModel? _animaliaReindeer;
    private B3dAnimatedModel? _animaliaWolf;
    private readonly GpuBuffers?[] _animalBuffers = new GpuBuffers?[ItemBufferSlots];

    public IReadOnlyList<AnimalEntity>? Animals { get; set; }

    public AnimalTextureLayers AnimalTextures { get; set; }

    /// <summary>Imported Luanti mob visuals, keyed by their canonical Lua entity IDs.</summary>
    public MobVisualRegistry? MobVisuals { get; set; }

    /// <summary>Model postavy hráče — Minetest Sam z Luanti.</summary>
    public B3dAnimatedModel? PlayerVisual { get; set; }

    /// <summary>Kde postava stojí, kam se dívá a který snímek animace hraje.</summary>
    public (Vector3 Position, float Yaw, float Frame, float HeadPitch)? Player { get; set; }

    /// <summary>Vrstva atlasu s kůží postavy a její výřez v dlaždici.</summary>
    /// <summary>
    /// Vrstva atlasu s kůží postavy. Používá ji hráč i kolonisté.
    /// </summary>
    /// <remarks>
    /// <b>Nula je platný index, a proto nebezpečný.</b> Když se tahle vlastnost zapomene
    /// nastavit, tiše se použije vrstva 0 — v abecedně tříděném atlasu <c>acacia_leaves</c>.
    /// Přesně to se stalo: kolonisté chodili po světě zelení, protože se vrstva nastavovala
    /// až v kamerové větvi, která se v první osobě vrátí dřív. Výchozí hodnota je proto −1,
    /// tedy „nikdo to nenastavil", a postavy se pak nekreslí vůbec — chybějící postava se
    /// hlásí sama, kdežto zelená vypadá jako záměr.
    /// </remarks>
    public float PlayerSkinLayer { get; set; } = -1f;

    /// <summary>Je vrstva kůže přihlášená? Bez ní se postavy kreslit nesmí.</summary>
    private bool HasSkinLayer => PlayerSkinLayer >= 0f;

    public Vector2 PlayerSkinUvScale { get; set; } = Vector2.One;

    /// <summary>Proceduralni kabelaz sveta. Vlastni ji herni vrstva a meni ji jen pri editaci site.</summary>
    public MeshBuffer? PowerMesh { get; set; }

    private readonly GpuBuffers?[] _powerBuffers = new GpuBuffers?[ItemBufferSlots];

    /// <summary>Pár dveří a trapdoorů právě otáčených mimo statickou mesh chunku.</summary>
    public MeshBuffer? AnimatedBuildingParts { get; set; }

    private readonly GpuBuffers?[] _buildingPartBuffers = new GpuBuffers?[ItemBufferSlots];

    private void DrawPower(
                VulkanPipeline pipeline,
        Span<float> frameConstants,
        RenderStats stats)
    {
        CommandBuffer commandBuffer = _renderer.CommandBuffer;
        if (PowerMesh is null || PowerMesh.IsEmpty)
        {
            return;
        }

        // Stejne jako u ruky se buffer strida podle rozpracovaneho snimku. Sit se sice meni
        // zridka, ale zmena muze prijit ve chvili, kdy GPU jeste cte predchozi obsah.
        int slot = _renderer.FrameSlot % ItemBufferSlots;
        _powerBuffers[slot] ??= NewBuffers();
        _powerBuffers[slot]!.Upload(PowerMesh);

        BindPass(commandBuffer, pipeline, frameConstants);
        DrawBuffers(commandBuffer, pipeline, Vector3i.Zero, _powerBuffers[slot]!, stats);
    }

    private void DrawAnimatedBuildingParts(
                Span<float> frameConstants,
        RenderStats stats)
    {
        CommandBuffer commandBuffer = _renderer.CommandBuffer;
        if (AnimatedBuildingParts is null || AnimatedBuildingParts.IsEmpty)
        {
            return;
        }

        int slot = _renderer.FrameSlot % ItemBufferSlots;
        _buildingPartBuffers[slot] ??= NewBuffers();
        _buildingPartBuffers[slot]!.Upload(AnimatedBuildingParts);

        // Mesh obsahuje obě fyzické strany tenkého panelu. Bez cullingu zůstává viditelná
        // i během průchodu přes přesných devadesát stupňů.
        BindPass(commandBuffer, _opaqueNoCull, frameConstants);
        DrawBuffers(commandBuffer, 
            _opaqueNoCull, Vector3i.Zero,
            _buildingPartBuffers[slot]!, stats);
    }

    private void DrawAnimals(
                Span<float> frameConstants,
        RenderStats stats)
    {
        CommandBuffer commandBuffer = _renderer.CommandBuffer;
        if (Animals is not { Count: > 0 } && Player is null && Colonists is not { Count: > 0 })
            return;

        _animalMesh.Clear();

        // Postava jde do téže dávky jako zvířata: stejná pipeline (cutout, bez cullingu),
        // stejný atlas, takže nestojí ani draw call navíc.
        if (Player is { } pose && PlayerVisual is not null && HasSkinLayer)
        {
            PlayerVisual.AppendAt(
                _animalMesh, pose.Position, pose.Yaw, pose.Frame, PlayerSkinLayer, PlayerSkinUvScale,
                ("Head", pose.HeadPitch));
        }

        // KOLONISTÉ JSOU POSTAVY, NE KRYCHLE. Kreslili se jako instancovaná kostka s natvrdo
        // psanou vrstvou atlasu, ze které se po přetřídění stala kůra — hráč pak ve světě
        // našel plovoucí dřevěné kostky a nepoznal, co to je. když nová
        // věc na obrazovce vypadá jako Minecraft, je to bug.
        if (Colonists is { Count: > 0 } colonists && PlayerVisual is not null && HasSkinLayer)
        {
            for (int i = 0; i < colonists.Count; i++)
            {
                (Vector3 position, float yaw, float frame) = colonists[i];
                PlayerVisual.AppendAt(_animalMesh, position, yaw, frame, PlayerSkinLayer, PlayerSkinUvScale);
            }
        }

        foreach (AnimalEntity animal in Animals ?? [])
        {
            if (MobVisuals?.Append(_animalMesh, animal) == true)
                continue;
            if (animal.Kind == AnimalKind.Sheep && _classicSheep is not null)
                _classicSheep.Append(_animalMesh, animal, AnimalTextures.SheepSkin);
            else if (animal.Kind == AnimalKind.Deer && _animaliaReindeer is not null)
                _animaliaReindeer.Append(_animalMesh, animal, AnimalTextures.DeerSkin);
            else if (animal.Kind == AnimalKind.Wolf && _animaliaWolf is not null)
                _animaliaWolf.Append(_animalMesh, animal, AnimalTextures.WolfFor(animal.Id));
            else
                AddAnimal(_animalMesh, animal, AnimalTextures);
        }

        if (_animalMesh.IsEmpty)
            return;

        int slot = _renderer.FrameSlot % ItemBufferSlots;
        _animalBuffers[slot] ??= NewBuffers();
        _animalBuffers[slot]!.Upload(_animalMesh);
        // Luanti mob skins contain transparent padding around their UV islands. The static
        // cutout pipeline keeps depth writes, drops those empty texels and disables culling for
        // old B3D meshes whose winding is not consistent with Tesseris terrain meshes.
        BindPass(commandBuffer, _items, frameConstants);
        DrawBuffers(commandBuffer, _items, Vector3i.Zero, _animalBuffers[slot]!, stats);
    }

    internal static void AddAnimal(
        MeshBuffer mesh,
        AnimalEntity animal,
        AnimalTextureLayers textures)
    {
        float gait = animal.Moving ? MathF.Sin(animal.WalkPhase) * 0.55f : 0f;
        float bob = animal.Moving ? MathF.Abs(MathF.Sin(animal.WalkPhase * 2f)) * 0.018f : 0f;
        float grazing = animal.HeadLowering;
        if (animal.Kind == AnimalKind.Sheep)
        {
            // Stejná hierarchie jako u klasického voxelového quadrupeda: trup, hlava a čtyři
            // samostatné nohy. Žádné deformované koule ani lidská kolena.
            AddAnimalBox(mesh, animal, new Vector3(0f, 0.86f + bob, -0.05f), new Vector3(0.43f, 0.31f, 0.59f), textures.SheepSkin, AnimalSkinPart.Body);
            AddAnimalBox(mesh, animal, new Vector3(0f, 0.88f + bob, -0.69f), new Vector3(0.11f, 0.1f, 0.12f), textures.SheepSkin, AnimalSkinPart.Tail, -0.25f);

            float headY = Mix(0.9f + bob, 0.56f, grazing);
            float headZ = Mix(0.62f, 0.74f, grazing);
            AddAnimalBox(mesh, animal, new Vector3(0f, headY, headZ), new Vector3(0.21f, 0.23f, 0.2f), textures.SheepSkin, AnimalSkinPart.Head, grazing * 0.45f);
            AddAnimalBox(mesh, animal, new Vector3(0f, headY - 0.06f, headZ + 0.2f), new Vector3(0.14f, 0.105f, 0.11f), textures.SheepSkin, AnimalSkinPart.Muzzle, grazing * 0.45f);
            AddAnimalBox(mesh, animal, new Vector3(-0.245f, headY + 0.08f, headZ - 0.01f), new Vector3(0.075f, 0.035f, 0.105f), textures.SheepSkin, AnimalSkinPart.Ear);
            AddAnimalBox(mesh, animal, new Vector3(0.245f, headY + 0.08f, headZ - 0.01f), new Vector3(0.075f, 0.035f, 0.105f), textures.SheepSkin, AnimalSkinPart.Ear);
            AddQuadrupedLeg(mesh, animal, new Vector3(-0.29f, 0.64f, -0.4f), 0.58f, 0.1f, gait, textures.SheepSkin);
            AddQuadrupedLeg(mesh, animal, new Vector3(0.29f, 0.64f, -0.4f), 0.58f, 0.1f, -gait, textures.SheepSkin);
            AddQuadrupedLeg(mesh, animal, new Vector3(-0.29f, 0.64f, 0.4f), 0.58f, 0.1f, -gait, textures.SheepSkin);
            AddQuadrupedLeg(mesh, animal, new Vector3(0.29f, 0.64f, 0.4f), 0.58f, 0.1f, gait, textures.SheepSkin);
            return;
        }

        if (animal.Kind == AnimalKind.Wolf)
        {
            float attack = animal.AttackPose;
            float headY = Mix(0.88f + bob, 0.76f, attack);
            float headZ = Mix(0.63f, 0.78f, attack);
            float wolfGait = gait * 1.25f;

            // Low, forward-weighted silhouette: broad chest, narrow waist, long muzzle and raised tail.
            // It remains readable as a predator even in moonlight and does not reuse the deer proportions.
            AddAnimalBox(mesh, animal, new Vector3(0f, 0.67f + bob, -0.05f), new Vector3(0.32f, 0.255f, 0.58f), textures.WolfSkin, AnimalSkinPart.Body);
            AddAnimalBox(mesh, animal, new Vector3(0f, 0.79f + bob, 0.48f), new Vector3(0.23f, 0.24f, 0.24f), textures.WolfSkin, AnimalSkinPart.Neck, -0.14f, new Vector3(0f, 0.69f, 0.35f));
            AddAnimalBox(mesh, animal, new Vector3(0f, headY, headZ), new Vector3(0.245f, 0.22f, 0.25f), textures.WolfSkin, AnimalSkinPart.Head, attack * 0.16f);
            AddAnimalBox(mesh, animal, new Vector3(0f, headY - 0.08f, headZ + 0.27f), new Vector3(0.16f, 0.12f, 0.18f), textures.WolfSkin, AnimalSkinPart.Muzzle, attack * 0.16f);
            AddAnimalBox(mesh, animal, new Vector3(-0.16f, headY + 0.245f, headZ - 0.02f), new Vector3(0.075f, 0.14f, 0.075f), textures.WolfSkin, AnimalSkinPart.Ear, -0.18f);
            AddAnimalBox(mesh, animal, new Vector3(0.16f, headY + 0.245f, headZ - 0.02f), new Vector3(0.075f, 0.14f, 0.075f), textures.WolfSkin, AnimalSkinPart.Ear, -0.18f);
            AddAnimalBox(mesh, animal, new Vector3(0f, 0.83f + bob, -0.68f), new Vector3(0.105f, 0.105f, 0.30f), textures.WolfSkin, AnimalSkinPart.Tail, 0.52f, new Vector3(0f, 0.72f, -0.48f));
            AddQuadrupedLeg(mesh, animal, new Vector3(-0.235f, 0.56f, -0.38f), 0.53f, 0.075f, wolfGait, textures.WolfSkin);
            AddQuadrupedLeg(mesh, animal, new Vector3(0.235f, 0.56f, -0.38f), 0.53f, 0.075f, -wolfGait, textures.WolfSkin);
            AddQuadrupedLeg(mesh, animal, new Vector3(-0.235f, 0.56f, 0.39f), 0.53f, 0.075f, -wolfGait, textures.WolfSkin);
            AddQuadrupedLeg(mesh, animal, new Vector3(0.235f, 0.56f, 0.39f), 0.53f, 0.075f, wolfGait, textures.WolfSkin);
            return;
        }

        AddAnimalBox(mesh, animal, new Vector3(0f, 1.0f + bob, -0.06f), new Vector3(0.38f, 0.3f, 0.62f), textures.DeerSkin, AnimalSkinPart.Body);
        AddAnimalBox(mesh, animal, new Vector3(0f, 1.24f + bob, 0.43f), new Vector3(0.16f, 0.31f, 0.16f), textures.DeerSkin, AnimalSkinPart.Neck, 0.2f, new Vector3(0f, 0.98f, 0.39f));
        AddAnimalBox(mesh, animal, new Vector3(0f, 1.08f + bob, -0.73f), new Vector3(0.1f, 0.075f, 0.17f), textures.DeerSkin, AnimalSkinPart.Tail, -0.38f, new Vector3(0f, 1.08f, -0.61f));

        float deerHeadY = Mix(1.52f + bob, 0.82f, grazing);
        float deerHeadZ = Mix(0.66f, 0.8f, grazing);
        AddAnimalBox(mesh, animal, new Vector3(0f, deerHeadY, deerHeadZ), new Vector3(0.22f, 0.2f, 0.25f), textures.DeerSkin, AnimalSkinPart.Head, grazing * 0.55f);
        AddAnimalBox(mesh, animal, new Vector3(0f, deerHeadY - 0.07f, deerHeadZ + 0.26f), new Vector3(0.14f, 0.1f, 0.14f), textures.DeerSkin, AnimalSkinPart.Muzzle, grazing * 0.55f);
        AddAnimalBox(mesh, animal, new Vector3(-0.24f, deerHeadY + 0.1f, deerHeadZ - 0.03f), new Vector3(0.09f, 0.04f, 0.13f), textures.DeerSkin, AnimalSkinPart.Ear, -0.14f);
        AddAnimalBox(mesh, animal, new Vector3(0.24f, deerHeadY + 0.1f, deerHeadZ - 0.03f), new Vector3(0.09f, 0.04f, 0.13f), textures.DeerSkin, AnimalSkinPart.Ear, -0.14f);
        AddQuadrupedLeg(mesh, animal, new Vector3(-0.25f, 0.79f, -0.43f), 0.73f, 0.095f, gait, textures.DeerSkin);
        AddQuadrupedLeg(mesh, animal, new Vector3(0.25f, 0.79f, -0.43f), 0.73f, 0.095f, -gait, textures.DeerSkin);
        AddQuadrupedLeg(mesh, animal, new Vector3(-0.25f, 0.79f, 0.43f), 0.73f, 0.095f, -gait, textures.DeerSkin);
        AddQuadrupedLeg(mesh, animal, new Vector3(0.25f, 0.79f, 0.43f), 0.73f, 0.095f, gait, textures.DeerSkin);
        if (grazing < 0.65f)
        {
            AddAntler(mesh, animal, -0.13f, deerHeadY + 0.2f, deerHeadZ, textures.DeerSkin);
            AddAntler(mesh, animal, 0.13f, deerHeadY + 0.2f, deerHeadZ, textures.DeerSkin);
        }
    }

    /// <summary>Installs the classic Mobs Animal sheep used by older Luanti games.</summary>
    public void LoadClassicSheep(string path) => _classicSheep = B3dAnimatedModel.Load(
        path,
        B3dAnimationProfile.ClassicMobsSheep,
        worldScale: 0.1f,
        worldOffset: new Vector3(0f, 1f, 0f));

    public void LoadAnimaliaReindeer(string path) => _animaliaReindeer = B3dAnimatedModel.Load(path);

    public void LoadAnimaliaWolf(string path) => _animaliaWolf = B3dAnimatedModel.Load(path);

    private static float Mix(float from, float to, float amount) => from + ((to - from) * amount);

    private static void AddQuadrupedLeg(
        MeshBuffer mesh, AnimalEntity animal, Vector3 hip,
        float length, float halfWidth, float pitch, float layer)
    {
        AddAnimalBox(
            mesh,
            animal,
            hip - new Vector3(0f, length * 0.5f, 0f),
            new Vector3(halfWidth, length * 0.5f, halfWidth),
            layer,
            AnimalSkinPart.Leg,
            pitch,
            hip);
    }

    private static void AddJointedLeg(
        MeshBuffer mesh, AnimalEntity animal, Vector3 hip, float swing, float layer,
        float upperLength, float lowerLength, float radius)
    {
        float stride = MathF.Sin(swing);
        Vector3 knee = hip + new Vector3(0f, -upperLength, stride * upperLength * 0.48f);
        Vector3 hoof = knee + new Vector3(0f, -lowerLength, -stride * lowerLength * 0.32f);
        AddAnimalTaperedSegment(mesh, animal, hip, knee, radius * 1.15f, radius, layer, 6);
        AddAnimalTaperedSegment(mesh, animal, knee, hoof, radius, radius * 0.78f, layer, 6);
        AddAnimalEllipsoid(mesh, animal, hoof + new Vector3(0f, 0.015f, 0.035f), new Vector3(radius * 1.05f, 0.055f, radius * 1.5f), layer, 6);
    }

    private static void AddAntler(MeshBuffer mesh, AnimalEntity animal, float x, float y, float z, float layer)
    {
        Vector3 root = new(x, y, z);
        Vector3 crown = root + new Vector3(x < 0f ? -0.04f : 0.04f, 0.32f, -0.03f);
        AddAnimalTaperedSegment(mesh, animal, root, crown, 0.035f, 0.024f, layer, 6);
        AddAnimalTaperedSegment(mesh, animal, crown - new Vector3(0f, 0.12f, 0f), crown + new Vector3(x < 0f ? -0.13f : 0.13f, 0.08f, 0.02f), 0.026f, 0.014f, layer, 6);
        AddAnimalTaperedSegment(mesh, animal, crown - new Vector3(0f, 0.03f, 0f), crown + new Vector3(x < 0f ? -0.1f : 0.1f, 0.12f, -0.04f), 0.024f, 0.012f, layer, 6);
    }

    private static void AddAnimalEllipsoid(
        MeshBuffer mesh, AnimalEntity animal, Vector3 centre, Vector3 radii, float layer, int sides)
    {
        const int rings = 7;
        Vector3 bottom = centre - new Vector3(0f, radii.Y, 0f);
        Vector3 top = centre + new Vector3(0f, radii.Y, 0f);
        for (int side = 0; side < sides; side++)
        {
            float a0 = MathF.Tau * side / sides;
            float a1 = MathF.Tau * (side + 1) / sides;
            Vector3 RingPoint(float angle, int ring)
            {
                float v = -1f + (2f * (ring + 1) / (rings + 1));
                float radius = MathF.Sqrt(Math.Max(0f, 1f - (v * v)));
                return centre + new Vector3(
                    MathF.Cos(angle) * radii.X * radius,
                    radii.Y * v,
                    MathF.Sin(angle) * radii.Z * radius);
            }

            Vector3 first0 = RingPoint(a0, 0);
            Vector3 first1 = RingPoint(a1, 0);
            mesh.AddTriangle(
                TransformAnimalPoint(animal, bottom), TransformAnimalPoint(animal, first0), TransformAnimalPoint(animal, first1),
                new Vector2(0.5f, 1f), new Vector2((float)side / sides, 0.82f), new Vector2((float)(side + 1) / sides, 0.82f),
                layer, 0.64f);

            for (int band = 0; band < rings - 1; band++)
            {
                float v0 = -1f + (2f * (band + 1) / (rings + 1));
                float v1 = -1f + (2f * (band + 2) / (rings + 1));
                Vector3 p00 = RingPoint(a0, band);
                Vector3 p10 = RingPoint(a1, band);
                Vector3 p11 = RingPoint(a1, band + 1);
                Vector3 p01 = RingPoint(a0, band + 1);
                float shade = 0.72f + (0.28f * Math.Max(0f, v1));
                mesh.AddQuad(
                    TransformAnimalPoint(animal, p00), TransformAnimalPoint(animal, p01),
                    TransformAnimalPoint(animal, p11), TransformAnimalPoint(animal, p10),
                    new Vector2((float)side / sides, (float)band / rings),
                    new Vector2((float)side / sides, (float)(band + 1) / rings),
                    new Vector2((float)(side + 1) / sides, (float)(band + 1) / rings),
                    new Vector2((float)(side + 1) / sides, (float)band / rings),
                    layer, shade, shade, shade, shade, false);
            }

            Vector3 last0 = RingPoint(a0, rings - 1);
            Vector3 last1 = RingPoint(a1, rings - 1);
            mesh.AddTriangle(
                TransformAnimalPoint(animal, top), TransformAnimalPoint(animal, last1), TransformAnimalPoint(animal, last0),
                new Vector2(0.5f, 0f), new Vector2((float)(side + 1) / sides, 0.18f), new Vector2((float)side / sides, 0.18f),
                layer, 1f);
        }
    }

    private static void AddAnimalTaperedSegment(
        MeshBuffer mesh, AnimalEntity animal, Vector3 start, Vector3 end,
        float startRadius, float endRadius, float layer, int sides)
    {
        AnimalUv(AnimalSkinPart.Antler, AnimalSkinFace.Front)
            .GetCorners(out Vector2 uv00, out Vector2 uv10, out Vector2 uv11, out Vector2 uv01);
        Vector3 direction = end - start;
        if (direction.LengthSquared < 0.000001f) return;
        direction.Normalize();
        Vector3 reference = MathF.Abs(direction.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX;
        Vector3 right = Vector3.Cross(direction, reference).Normalized();
        Vector3 forward = Vector3.Cross(right, direction).Normalized();
        for (int side = 0; side < sides; side++)
        {
            float a0 = MathF.Tau * side / sides;
            float a1 = MathF.Tau * (side + 1) / sides;
            Vector3 radial0 = (right * MathF.Cos(a0)) + (forward * MathF.Sin(a0));
            Vector3 radial1 = (right * MathF.Cos(a1)) + (forward * MathF.Sin(a1));
            Vector3 p0 = start + (radial0 * startRadius);
            Vector3 p1 = start + (radial1 * startRadius);
            Vector3 p2 = end + (radial1 * endRadius);
            Vector3 p3 = end + (radial0 * endRadius);
            float shade = 0.72f + (0.23f * MathF.Max(0f, radial0.Y));
            Vector3 normal = Vector3.Cross(p1 - p0, p2 - p0);
            bool outward = Vector3.Dot(normal, radial0 + radial1) >= 0f;
            if (outward)
            {
                mesh.AddQuad(
                    TransformAnimalPoint(animal, p0), TransformAnimalPoint(animal, p1),
                    TransformAnimalPoint(animal, p2), TransformAnimalPoint(animal, p3),
                    uv00, uv10, uv11, uv01,
                    layer, shade, shade, shade, shade, false);
            }
            else
            {
                mesh.AddQuad(
                    TransformAnimalPoint(animal, p0), TransformAnimalPoint(animal, p3),
                    TransformAnimalPoint(animal, p2), TransformAnimalPoint(animal, p1),
                    uv00, uv01, uv11, uv10,
                    layer, shade, shade, shade, shade, false);
            }

            mesh.AddTriangle(
                TransformAnimalPoint(animal, start), TransformAnimalPoint(animal, p1), TransformAnimalPoint(animal, p0),
                (uv00 + uv11) * 0.5f, uv11, uv01, layer, 0.68f);
            mesh.AddTriangle(
                TransformAnimalPoint(animal, end), TransformAnimalPoint(animal, p3), TransformAnimalPoint(animal, p2),
                (uv00 + uv11) * 0.5f, uv00, uv10, layer, 0.82f);
        }
    }

    private static Vector3 TransformAnimalPoint(AnimalEntity animal, Vector3 local)
    {
        Vector3 origin = animal.VisualInitialized ? animal.RenderPosition : animal.Position;
        float yaw = animal.VisualInitialized ? animal.RenderYaw : animal.Yaw;
        float cosine = MathF.Cos(yaw);
        float sine = MathF.Sin(yaw);
        return origin + new Vector3(
            (local.X * cosine) + (local.Z * sine), local.Y,
            (local.Z * cosine) - (local.X * sine));
    }

    private enum AnimalSkinPart { Body, Head, Muzzle, Leg, Neck, Ear, Tail, Antler }

    private enum AnimalSkinFace { Front, Back, Left, Right, Top, Bottom }

    private readonly record struct AnimalUvRect(int X, int Y, int Width, int Height)
    {
        public void GetCorners(out Vector2 uv00, out Vector2 uv10, out Vector2 uv11, out Vector2 uv01)
        {
            const float size = 64f;
            float u0 = X / size;
            float u1 = (X + Width) / size;
            float v0 = Y / size;
            float v1 = (Y + Height) / size;
            uv00 = new Vector2(u0, v1);
            uv10 = new Vector2(u1, v1);
            uv11 = new Vector2(u1, v0);
            uv01 = new Vector2(u0, v0);
        }
    }

    private static AnimalUvRect AnimalUv(AnimalSkinPart part, AnimalSkinFace face) => (part, face) switch
    {
        (AnimalSkinPart.Body, AnimalSkinFace.Front) => new(0, 0, 12, 10),
        (AnimalSkinPart.Body, AnimalSkinFace.Back) => new(12, 0, 12, 10),
        (AnimalSkinPart.Body, AnimalSkinFace.Left) => new(24, 0, 12, 10),
        (AnimalSkinPart.Body, AnimalSkinFace.Right) => new(36, 0, 12, 10),
        (AnimalSkinPart.Body, AnimalSkinFace.Top) => new(48, 0, 12, 10),
        (AnimalSkinPart.Body, AnimalSkinFace.Bottom) => new(0, 10, 12, 10),
        (AnimalSkinPart.Head, AnimalSkinFace.Front) => new(12, 10, 10, 10),
        (AnimalSkinPart.Head, AnimalSkinFace.Back) => new(22, 10, 10, 10),
        (AnimalSkinPart.Head, AnimalSkinFace.Left) => new(32, 10, 10, 10),
        (AnimalSkinPart.Head, AnimalSkinFace.Right) => new(42, 10, 10, 10),
        (AnimalSkinPart.Head, AnimalSkinFace.Top) => new(52, 10, 10, 10),
        (AnimalSkinPart.Head, AnimalSkinFace.Bottom) => new(0, 20, 10, 10),
        (AnimalSkinPart.Muzzle, AnimalSkinFace.Front) => new(10, 20, 8, 8),
        (AnimalSkinPart.Muzzle, AnimalSkinFace.Back) => new(18, 20, 8, 8),
        (AnimalSkinPart.Muzzle, AnimalSkinFace.Left) => new(26, 20, 8, 8),
        (AnimalSkinPart.Muzzle, AnimalSkinFace.Right) => new(34, 20, 8, 8),
        (AnimalSkinPart.Muzzle, AnimalSkinFace.Top) => new(42, 20, 8, 8),
        (AnimalSkinPart.Muzzle, AnimalSkinFace.Bottom) => new(50, 20, 8, 8),
        (AnimalSkinPart.Leg, AnimalSkinFace.Front) => new(0, 30, 6, 12),
        (AnimalSkinPart.Leg, AnimalSkinFace.Back) => new(6, 30, 6, 12),
        (AnimalSkinPart.Leg, AnimalSkinFace.Left) => new(12, 30, 6, 12),
        (AnimalSkinPart.Leg, AnimalSkinFace.Right) => new(18, 30, 6, 12),
        (AnimalSkinPart.Leg, AnimalSkinFace.Top) => new(24, 30, 6, 6),
        (AnimalSkinPart.Leg, AnimalSkinFace.Bottom) => new(30, 30, 6, 6),
        (AnimalSkinPart.Neck, AnimalSkinFace.Front) => new(36, 30, 8, 12),
        (AnimalSkinPart.Neck, AnimalSkinFace.Back) => new(44, 30, 8, 12),
        (AnimalSkinPart.Neck, AnimalSkinFace.Left) => new(52, 30, 6, 12),
        (AnimalSkinPart.Neck, AnimalSkinFace.Right) => new(58, 30, 6, 12),
        (AnimalSkinPart.Neck, AnimalSkinFace.Top) => new(24, 36, 6, 6),
        (AnimalSkinPart.Neck, AnimalSkinFace.Bottom) => new(30, 36, 6, 6),
        (AnimalSkinPart.Ear, AnimalSkinFace.Front) => new(0, 42, 8, 4),
        (AnimalSkinPart.Ear, AnimalSkinFace.Back) => new(8, 42, 8, 4),
        (AnimalSkinPart.Ear, AnimalSkinFace.Left) => new(16, 42, 8, 4),
        (AnimalSkinPart.Ear, AnimalSkinFace.Right) => new(24, 42, 8, 4),
        (AnimalSkinPart.Ear, AnimalSkinFace.Top) => new(32, 42, 8, 4),
        (AnimalSkinPart.Ear, AnimalSkinFace.Bottom) => new(40, 42, 8, 4),
        (AnimalSkinPart.Tail, AnimalSkinFace.Front) => new(0, 46, 8, 8),
        (AnimalSkinPart.Tail, AnimalSkinFace.Back) => new(8, 46, 8, 8),
        (AnimalSkinPart.Tail, AnimalSkinFace.Left) => new(16, 46, 8, 8),
        (AnimalSkinPart.Tail, AnimalSkinFace.Right) => new(24, 46, 8, 8),
        (AnimalSkinPart.Tail, AnimalSkinFace.Top) => new(32, 46, 8, 8),
        (AnimalSkinPart.Tail, AnimalSkinFace.Bottom) => new(40, 46, 8, 8),
        (AnimalSkinPart.Antler, AnimalSkinFace.Front) => new(48, 46, 8, 8),
        (AnimalSkinPart.Antler, AnimalSkinFace.Back) => new(56, 46, 8, 8),
        (AnimalSkinPart.Antler, AnimalSkinFace.Left) => new(0, 54, 8, 8),
        (AnimalSkinPart.Antler, AnimalSkinFace.Right) => new(8, 54, 8, 8),
        (AnimalSkinPart.Antler, AnimalSkinFace.Top) => new(16, 54, 8, 8),
        _ => new(24, 54, 8, 8)
    };

    private static void AddAnimalBox(
        MeshBuffer mesh, AnimalEntity animal, Vector3 centre, Vector3 half, float layer,
        AnimalSkinPart skinPart, float pitch = 0f, Vector3? pivot = null)
    {
        Vector3 origin = animal.VisualInitialized ? animal.RenderPosition : animal.Position;
        float yaw = animal.VisualInitialized ? animal.RenderYaw : animal.Yaw;
        float cosine = MathF.Cos(yaw);
        float sine = MathF.Sin(yaw);
        float pitchCosine = MathF.Cos(pitch);
        float pitchSine = MathF.Sin(pitch);
        Vector3 joint = pivot ?? centre;
        Vector3 Point(float x, float y, float z)
        {
            x += centre.X;
            y += centre.Y;
            z += centre.Z;
            float relativeY = y - joint.Y;
            float relativeZ = z - joint.Z;
            y = joint.Y + (relativeY * pitchCosine) - (relativeZ * pitchSine);
            z = joint.Z + (relativeY * pitchSine) + (relativeZ * pitchCosine);
            return origin + new Vector3(
                (x * cosine) + (z * sine), y, (z * cosine) - (x * sine));
        }

        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, float shade, AnimalSkinFace face)
        {
            AnimalUv(skinPart, face).GetCorners(out Vector2 uv00, out Vector2 uv10, out Vector2 uv11, out Vector2 uv01);
            mesh.AddQuad(a, b, c, d, uv00, uv10, uv11, uv01, layer, shade, shade, shade, shade, false);
        }

        float xSide = FaceShading.ForAxis(0, positive: true);
        float top = FaceShading.ForAxis(1, positive: true);
        float front = FaceShading.ForAxis(2, positive: true);
        float hx = half.X; float hy = half.Y; float hz = half.Z;
        Quad(Point(-hx, -hy, hz), Point(hx, -hy, hz), Point(hx, hy, hz), Point(-hx, hy, hz), front, AnimalSkinFace.Front);
        Quad(Point(hx, -hy, -hz), Point(-hx, -hy, -hz), Point(-hx, hy, -hz), Point(hx, hy, -hz), front, AnimalSkinFace.Back);
        Quad(Point(-hx, -hy, -hz), Point(-hx, -hy, hz), Point(-hx, hy, hz), Point(-hx, hy, -hz), xSide, AnimalSkinFace.Left);
        Quad(Point(hx, -hy, hz), Point(hx, -hy, -hz), Point(hx, hy, -hz), Point(hx, hy, hz), xSide, AnimalSkinFace.Right);
        Quad(Point(-hx, hy, hz), Point(hx, hy, hz), Point(hx, hy, -hz), Point(-hx, hy, -hz), top, AnimalSkinFace.Top);
        Quad(Point(-hx, -hy, -hz), Point(hx, -hy, -hz), Point(hx, -hy, hz), Point(-hx, -hy, hz), top * 0.7f, AnimalSkinFace.Bottom);
    }

    /// <summary>
    /// Nakreslí předměty ležící ve světě.
    /// </summary>
    /// <remarks>
    /// <para><b>Mesh se staví každý snímek znovu.</b> Předměty padají, otáčejí se a mizí,
    /// takže by cache stejně platila jeden snímek — a jde o desítky krychliček, ne o svět.
    /// Buffer se ale <b>nealokuje</b> znovu: drží se jeden a jen se přepisuje.</para>
    ///
    /// <para>Kreslí se výřezovým průchodem, protože ikony nástrojů jsou z větší části
    /// průhledné. Krychlička nese na všech stěnách tutéž vrstvu — je moc malá na to, aby
    /// se na ní poznalo, že blok má jinou vrchní stěnu než bok.</para>
    /// </remarks>
    /// <summary>
    /// Instancované kreslení itemů: jedna geometrie, tisíce poloh, jeden draw call.
    /// </summary>
    /// <remarks>
    /// Plní se přes <see cref="AddInstancedItem"/> ještě před kreslením. Prázdný seznam
    /// nestojí nic a nevydá se ani draw call.
    /// </remarks>
    private void DrawInstancedItems(
        CommandBuffer commandBuffer, ReadOnlySpan<float> frameConstants, RenderStats stats)
    {
        if (_instancedItems is null || _instancedItems.Count == 0)
        {
            return;
        }

        // Prvních 24 floatů rámcových konstant má přesně to rozvržení, které shader itemů
        // čeká: matice, poloha kamery, začátek mlhy, barva mlhy a její konec. Skládat si
        // vlastní blok by znamenalo držet dvě kopie téhož a čekat, až se rozejdou.
        int calls = _instancedItems.Draw(commandBuffer, _instancedItemsDescriptor, frameConstants[..24]);
        for (int i = 0; i < calls; i++)
        {
            stats.CountDrawCall();
        }
    }

    /// <summary>Zařadí item ke kreslení. Co není v záběru, se zahodí (pravidlo 6.7).</summary>
    public bool AddInstancedItem(Frustum frustum, Vector3 centre, float scale, float layer, float light) =>
        _instancedItems is not null && _instancedItems.Add(frustum, centre, scale, layer, light);

    /// <summary>Zařadí item bez kontroly záběru. Jen pro měření nejhoršího případu.</summary>
    public bool AddInstancedItemUnculled(Vector3 centre, float scale, float layer, float light) =>
        _instancedItems is not null && _instancedItems.AddUnculled(centre, scale, layer, light);

    /// <summary>Začne sbírat instancované itemy pro nový snímek.</summary>
    public void BeginInstancedItems() => _instancedItems?.BeginFrame();

    /// <summary>Kolik instancovaných itemů se kreslilo naposledy.</summary>
    public int InstancedItemCount => _instancedItems?.Count ?? 0;

    /// <summary>Kolik instancovaných itemů se zahodilo mimo záběr.</summary>
    public int InstancedItemsCulled => _instancedItems?.CulledCount ?? 0;

    /// <summary>Kolik ms zabralo nahrání instancí na grafiku.</summary>
    public double InstancedItemsUploadMs => _instancedItems?.LastUploadMs ?? 0.0;

    public void DrawItems(
        IReadOnlyList<ItemEntity> entities, ItemRegistry items,
        Span<float> frameConstants, RenderStats stats)
    {
        CommandBuffer commandBuffer = _renderer.CommandBuffer;
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(stats);

        if (entities.Count == 0 && Held is null)
        {
            return;
        }

        _itemMesh.Clear();
        _heldMesh.Clear();

        // RUKA MÁ VLASTNÍ DÁVKU, i když je to táž pipeline a táž textura.
        //
        // Kreslí se totiž se zmáčknutým rozsahem hloubky, aby ji nemohlo nic zakrýt —
        // viz DrawHeld. To se nastavuje na celý draw call, takže ležící předměty, které
        // zakrývat SMÍ, musí jít zvlášť.
        if (Held is { } held)
        {
            AddHeld(_heldMesh, held);

            // Svetlo se vzorkuje tam, kde je predmet opravdu nakresleny, ne v pocatku
            // sveta ani u nohou hrace. Stejny prostorovy sampler pouzivaji predmety na
            // zemi, takze pochoden, stena i vzdalenost maji shodny vliv na obe cesty.
            Vector3 heldLightPosition = held.Eye
                + (held.Forward * 0.48f)
                + (held.Right * 0.20f)
                - (held.Up * 0.22f);
            float heldLight = Math.Clamp(
                DropLightSampler?.Invoke(heldLightPosition) ?? 1f,
                0f,
                1f);
            _heldMesh.SetBlockLight(heldLight);
            _heldMesh.MultiplyShade(Math.Clamp(
                SkyLightSampler?.Invoke(heldLightPosition) ?? 1f,
                0f,
                1f));
        }

        foreach (ItemEntity entity in entities)
        {
            // Otáčení a houpání. Bez nich je z předmětu na zemi kostička, která splyne
            // s terénem; pohyb je jediné, čím na sebe upozorní.
            float spin = entity.Age * 1.6f;
            float bob = MathF.Sin(entity.Age * 2.4f) * 0.05f;

            Vector3 centre = entity.Position + new Vector3(0f, 0.12f + bob, 0f);
            float layer = items.IconLayer(entity.Stack.Item);
            float blockLight = Math.Clamp(DropLightSampler?.Invoke(centre) ?? 1f, 0f, 1f);
            float skyLight = Math.Clamp(SkyLightSampler?.Invoke(centre) ?? 1f, 0f, 1f);

            if (items.IsFlat(entity.Stack.Item) || ItemModels?.ContainsKey(entity.Stack.Item) == true)
            {
                AddItemModel(_itemMesh, ShapeFor(entity.Stack.Item, (int)layer), centre, spin,
                    layer, skyLight, blockLight);
            }
            else
            {
                AddItemCube(_itemMesh, centre, spin, layer, skyLight, blockLight);
            }
        }

        DrawHeld(frameConstants, stats);

        if (_itemMesh.IsEmpty)
        {
            return;
        }

        // BUFFER NA KAŽDÝ ROZPRACOVANÝ SNÍMEK, ne jeden sdílený.
        //
        // Chunk se nahraje jednou a pak leží desítky snímků, takže mu jeden buffer stačí.
        // Předměty a hlavně RUKA se ale přepisují každý snímek — a grafika ještě čte ten
        // předchozí. Do jednoho bufferu se tím psalo pod rukama a bylo to vidět přesně tak,
        // jak to bylo nahlášeno: nástroj v ruce skákal a blikal při pohybu i při otáčení.
        //
        // Chunků jsou tisíce, takže u nich by dvojnásobek paměti stál za řeč; tohle je
        // jedna mesh na snímek.
        int slot = _renderer.FrameSlot % ItemBufferSlots;

        _itemBuffers[slot] ??= NewBuffers();
        _itemBuffers[slot]!.Upload(_itemMesh);

        BindPass(commandBuffer, _items, frameConstants);
        DrawBuffers(commandBuffer, _items, Vector3i.Zero, _itemBuffers[slot]!, stats);
    }

    /// <summary>
    /// Nakreslí praskliny na těženém bloku.
    /// </summary>
    /// <remarks>
    /// <para>Krychle je o kousek větší než blok, jinak by se její stěny praly o hloubku
    /// s tím, co je pod nimi, a praskliny by se objevovaly a mizely podle úhlu pohledu.</para>
    ///
    /// <para>Kreslí se výřezovým průchodem: textura je z větší části průhledná a čáry jsou
    /// černé, takže jsou vidět na kameni i na písku. Jediná barva, která funguje na obojím,
    /// je ztmavení.</para>
    /// </remarks>
    public void DrawBreaking(Span<float> frameConstants, RenderStats stats)
    {
        CommandBuffer commandBuffer = _renderer.CommandBuffer;
        ArgumentNullException.ThrowIfNull(stats);

        if (Breaking is not { } breaking || CrackLayer < 0 || breaking.Progress <= 0f)
        {
            return;
        }

        // Deset stupňů. Poslední se drží až do rozpadu, proto se ořezává na devítku.
        int stage = Math.Clamp((int)(breaking.Progress * 10f), 0, 9);

        _crackMesh.Clear();
        AddCrackCube(_crackMesh, breaking.Block, breaking.Bounds, CrackLayer + stage);

        _crackBuffers ??= NewBuffers();
        _crackBuffers.Upload(_crackMesh);

        // MÍCHANÝM PRŮCHODEM, NE VÝŘEZEM. Prasklina má odstupňovanou průhlednost — jádro
        // je hlubší a tmavší než okraj. Výřez alfu jen prahuje, takže by z toho byla plná
        // šedá čára a celé odstupňování by přišlo vniveč.
        // Bez cullingu ze stejného důvodu jako u předmětů. Odvrácené stěny krychle prasklin
        // leží uvnitř bloku, takže je stejně zahodí hloubkový test.
        BindPass(commandBuffer, _transparentNoCull, frameConstants);
        DrawBuffers(commandBuffer, _transparentNoCull, Vector3i.Zero, _crackBuffers, stats);
    }

    /// <summary>Krychle přes objem bloku, o chlup větší, se všemi šesti stěnami.</summary>
    private static void AddCrackCube(MeshBuffer mesh, Vector3i block, Aabb bounds, float layer)
    {
        const float Bulge = 0.004f;

        float x0 = block.X + bounds.Min.X - Bulge;
        float y0 = block.Y + bounds.Min.Y - Bulge;
        float z0 = block.Z + bounds.Min.Z - Bulge;
        float x1 = block.X + bounds.Max.X + Bulge;
        float y1 = block.Y + bounds.Max.Y + Bulge;
        float z1 = block.Z + bounds.Max.Z + Bulge;

        var uv00 = new Vector2(0f, 1f);
        var uv10 = new Vector2(1f, 1f);
        var uv11 = new Vector2(1f, 0f);
        var uv01 = new Vector2(0f, 0f);

        // Jas je všude stejný: prasklina není osvětlená plocha, ale díra v materiálu.
        const float Shade = 1f;

        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d) =>
            mesh.AddQuad(a, b, c, d, uv00, uv10, uv11, uv01, layer, Shade, Shade, Shade, Shade, false);

        Quad(new(x0, y0, z1), new(x1, y0, z1), new(x1, y1, z1), new(x0, y1, z1));
        Quad(new(x1, y0, z0), new(x0, y0, z0), new(x0, y1, z0), new(x1, y1, z0));
        Quad(new(x1, y0, z1), new(x1, y0, z0), new(x1, y1, z0), new(x1, y1, z1));
        Quad(new(x0, y0, z0), new(x0, y0, z1), new(x0, y1, z1), new(x0, y1, z0));
        Quad(new(x0, y1, z1), new(x1, y1, z1), new(x1, y1, z0), new(x0, y1, z0));
        Quad(new(x0, y0, z0), new(x1, y0, z0), new(x1, y0, z1), new(x0, y0, z1));
    }

    /// <summary>
    /// Co má hráč v ruce a odkud se na to dívá. Nastavuje okno každý snímek.
    /// </summary>
    /// <summary>
    /// Čím se to, co hráč drží, kreslí.
    /// </summary>
    /// <remarks>
    /// <b>Tři různé tvary, ne dva.</b> Původně se ruka kreslila týmž vytažením z ikony jako
    /// nástroje — jenže vytažení dává <b>desku</b>, což je u meče a krumpáče správně a u ruky
    /// úplně mimo. Bylo to nahlášeno stručně a přesně: „proč ta ruka je furt tak placatá".
    /// Ruka je od té doby kvádr.
    /// </remarks>
    public enum HeldKind
    {
        /// <summary>Ruka: podlouhlý kvádr, protože předloktí má objem.</summary>
        Hand,

        /// <summary>Plochý předmět: deska vytažená z obrysu ikony.</summary>
        Flat,

        /// <summary>Blok: krychle s texturou bloku.</summary>
        Block,

        /// <summary>Slab nebo schody slozene z osmi casti bloku.</summary>
        ShapedBlock,
    }

    /// <param name="Item">Index předmětu. Podle něj se hledá ruční model.</param>
    /// <param name="Layer">Vrstva textury: předmět, nebo ruka, když je prázdná.</param>
    /// <param name="Kind">Čím se to kreslí.</param>
    /// <param name="Swing">Rozmach, 0 až 1. Nula znamená klid.</param>
    public readonly record struct HeldItem(
        int Item, int Layer, HeldKind Kind,
        Vector3 Eye, Vector3 Right, Vector3 Up, Vector3 Forward, float Swing,
        byte Pieces = PieceMask.Full);

    /// <summary>
    /// Kolonisté ke kreslení: kde stojí, kam koukají a v jaké fázi je jejich chůze.
    /// </summary>
    /// <remarks>
    /// <b>Jdou do téže dávky jako zvířata a hráč</b> — stejná pipeline, stejný atlas, stejný
    /// model postavy. Sto kolonistů proto nestojí ani jeden draw call navíc.
    /// </remarks>
    public IReadOnlyList<(Vector3 Position, float Yaw, float Frame)>? Colonists { get; set; }

    /// <summary>Co drží hráč. <c>null</c> znamená, že se nekreslí nic.</summary>
    public HeldItem? Held { get; set; }

    /// <summary>Poloviční šířka předmětu ležícího na zemi.</summary>
    private const float ItemHalf = 0.16f;

    /// <summary>
    /// Tloušťka vytaženého předmětu.
    /// </summary>
    /// <remarks>
    /// Zhruba texel a půl. Tenčí deska se z boku ztratí, tlustší vypadá jako cihla —
    /// nástroj má být plochý, jen ne nekonečně.
    /// </remarks>
    private const float ItemThickness = 0.035f;

    /// <summary>Tělesa předmětů podle vrstvy textury. Počítají se jednou, viz <see cref="ItemShape"/>.</summary>
    private readonly Dictionary<int, ItemShape> _itemShapes = [];

    /// <summary>
    /// Ruční modely podle předmětu. Přebíjejí vytažení z obrysu.
    /// </summary>
    /// <remarks>
    /// Nastavuje okno při startu. Předmět bez záznamu se pořád vytahuje z ikony — ruční
    /// model má smysl jen tam, kde má věc mít tvar, který z plochého obrázku nevyjde.
    /// </remarks>
    public IReadOnlyDictionary<int, ItemShape>? ItemModels { get; set; }

    /// <summary>Těleso předmětu: ruční model, jinak vytažení z obrysu.</summary>
    private ItemShape ShapeFor(int item, int layer)
    {
        if (ItemModels is not null && ItemModels.TryGetValue(item, out ItemShape? model))
        {
            return model;
        }

        if (_itemShapes.TryGetValue(layer, out ItemShape? shape))
        {
            return shape;
        }

        shape = ItemShape.Extrude(_textures.Silhouette(layer), ItemHalf, ItemThickness);
        _itemShapes[layer] = shape;

        return shape;
    }

    /// <summary>
    /// Nakreslí to, co má hráč v ruce.
    /// </summary>
    /// <remarks>
    /// <para><b>Geometrie se staví rovnou ve světě, ne přes vlastní projekci.</b> Ruka se
    /// složí z os kamery a položí se pár decimetrů před oko, takže se kreslí týmž průchodem
    /// a týmiž konstantami jako všechno ostatní. Vlastní projekce pro pohled z ruky by
    /// znamenala druhý průchod s vymazanou hloubkou — a hloubka je od rozdělení snímku jen
    /// ke čtení, takže by se to muselo přestavět celé.</para>
    ///
    /// <para><b>Cena je známá:</b> ruka se zaboří do stěny, ke které si hráč stoupne úplně
    /// těsně. Je to vidět zřídka a stojí to nula rizika ve vulkanové části, kde jsou ještě
    /// nedodělky.</para>
    ///
    /// <para>Kreslí se spolu s ležícími předměty, tedy před vodou: pod hladinou má být
    /// ruka za ní, ne přes ni.</para>
    /// </remarks>
    private void AddHeld(MeshBuffer mesh, in HeldItem held)
    {
        // ROZMACH PODLE LUANTI.
        //
        // Křivky jsou doslova ty z `Camera::update` (references/luanti/src/client/camera.cpp:
        // 524-537). Luanti je počítá v jednotkách své wield scény, kde ruka sedí na
        // (55, −35, 65); u nás je základ v blocích, takže se posuny přepočítávají měřítkem
        // odvozeným z téhož poměru (0,33 bloku na 55 jednotek).
        //
        // Podstatné je, že to není jeden sinus: X jede přes `sin(f^0,8 · π)`, tedy prudce
        // dolů a pomaleji zpět, kdežto Y přes `sin(f · 1,8π)`, což je půldruhé periody —
        // ruka se cestou nadzvedne a teprve pak klesne. Právě tenhle nesoulad obou os dělá
        // ten typický luantovský oblouk, který jedním sinem nevznikne.
        const float LuantiUnit = 0.33f / 55f;
        float digfrac = Math.Clamp(held.Swing, 0f, 1f);
        bool digging = digfrac > 0f;

        float digX = digging ? -50f * MathF.Sin(MathF.Pow(digfrac, 0.8f) * MathF.PI) : 0f;
        float digY = digging ? 24f * MathF.Sin(digfrac * 1.8f * MathF.PI) : 0f;
        float digZ = digging ? 12.5f : 0f;

        // Náklon jde s `sin(digfrac · π)` — v Luanti je to slerp mezi dvěma orientacemi
        // řízený týmž činitelem.
        float swing = MathF.Sin(digfrac * MathF.PI);

        // KAM SE RUKA POLOŽÍ.
        //
        // Do pravého dolního rohu, ne doprostřed. První verze ji měla blízko a velkou,
        // takže zabírala čtvrtinu obrazu a zakrývala i to, na co hráč míří. Dál od oka
        // a víc do rohu z ní udělá to, co má být: doprovod pohledu, ne jeho překážka.
        Vector3 origin = held.Eye
            + (held.Forward * (0.58f + (digZ * LuantiUnit)))
            + (held.Right * (0.33f + (digX * LuantiUnit)))
            - (held.Up * (0.30f - (digY * LuantiUnit)));

        // Osy tělesa. Svislá se naklání dopředu, aby předmět mířil od hráče pryč a nestál
        // v obraze jako cedule; při rozmachu se naklopí ještě víc.
        Vector3 along = Vector3.Normalize((held.Up * (0.86f - (swing * 0.5f))) + (held.Forward * (0.5f + (swing * 0.5f))));
        Vector3 across = held.Right;

        // NÁSTROJ SE DRŽÍ NAHRANU, NE NAPLOCHO.
        //
        // Model má délku v ose Y a je tenký v ose Z. Když se Z namíří do strany, kouká na
        // hráče široká plocha čepele a nástroj vypadá jako placka přilepená na obrazovku —
        // přesně tak to bylo nahlášeno. Otočením o devadesát stupňů kolem vlastní délky
        // se tenká osa otočí k hráči a je vidět profil, jak se nářadí drží.
        Vector3 ax = Vector3.Normalize(Vector3.Cross(along, across));
        Vector3 ay = along;
        Vector3 az = across;

        // Zmenšeno z 1,35. Při té velikosti byla ruka v obraze větší než blok, na který
        // hráč sahá — a proti bloku se právě velikost ruky čte jako měřítko celého světa.
        const float Scale = 0.72f;

        Vector3 Place(Vector3 local) =>
            origin + (ax * local.X * Scale) + (ay * local.Y * Scale) + (az * local.Z * Scale);

        float layer = held.Layer;

        switch (held.Kind)
        {
            case HeldKind.Hand:
                // Minecraftový poměr paže je 4 × 12 × 4. Samostatná cesta je nutná:
                // obyčejný AddHeldBox lepí celou dlaždici na každou stěnu, kdežto ruka
                // používá skutečný rozbalený UV atlas se čtyřmi boky a dvěma čely.
                AddHeldArm(mesh, Place, layer, HandUvMin, HandUvSize);
                return;

            case HeldKind.Block:
                // Blok se nese jako kostka. Vytažení z ikony by z něj udělalo desku,
                // protože jeho ikona je plná dlaždice.
                AddHeldBox(mesh, Place, layer, ItemHalf * 0.85f, ItemHalf * 0.85f, ItemHalf * 0.85f);
                return;

            case HeldKind.ShapedBlock:
                AddHeldPieces(mesh, Place, layer, held.Pieces, ItemHalf * 0.85f);
                return;

            default:
                foreach (ItemShape.Face face in ShapeFor(held.Item, held.Layer).Faces)
                {
                    // Stěna si může nést vlastní vrstvu: ruční model má texturu rozřezanou
                    // na pruhy, takže každá stěna sedí jinde.
                    float faceLayer = face.Layer >= 0 ? face.Layer : layer;

                    mesh.AddQuad(
                        Place(face.P0), Place(face.P1), Place(face.P2), Place(face.P3),
                        face.U0, face.U1, face.U2, face.U3,
                        faceLayer, face.Shade, face.Shade, face.Shade, face.Shade, false);
                }

                return;
        }
    }

    /// <summary>
    /// Nakreslí ruku ve zmáčknutém rozsahu hloubky, aby ji nemohlo nic zakrýt.
    /// </summary>
    /// <remarks>
    /// <para><b>Ruku nesmí ukusovat svět.</b> Kreslí se jako obyčejná geometrie kousek před
    /// okem, takže o hloubku soupeřila se vším, co je blíž — a hlavně s <b>trávou</b>, která
    /// se hýbe větrným shaderem. Stačilo stoupnout si do porostu a stébla se do nástroje
    /// každý snímek zakusovala jinak. Hlásilo se to jako blikání při pohybu a bylo to ono:
    /// stébla, ne nástroj.</para>
    ///
    /// <para><b>Řešení je rozsah hloubky, ne vypnutý test.</b> Vypnout test by rozbilo
    /// zakrývání uvnitř nástroje samotného — zadní stěny by se kreslily přes přední.
    /// Zmáčknutím do nejbližších pár procent si ruka pořadí svých vlastních stěn udrží
    /// a přitom je vždycky blíž než cokoli ve světě.</para>
    ///
    /// <para>Rozsah se hned vrací zpátky: viewport je stav příkazového bufferu a všechno
    /// za tímhle voláním by ho zdědilo.</para>
    /// </remarks>
    /// <summary>
    /// Projekce pro ruku a nesený předmět, už převedená do vulkanského prostoru.
    /// <c>null</c> = kreslí se stejnou projekcí jako svět.
    /// </summary>
    /// <remarks>
    /// Luanti má na wield mesh vlastní scénu a vlastní kameru (<c>m_wieldmgr</c>), takže se
    /// nesený předmět nemění, když si hráč přenastaví zorný úhel. U nás se ruka kreslila
    /// projekcí světa, takže při širokém FOV odjela do rohu a protáhla se.
    /// </remarks>
    public Matrix4? HeldViewProjection { get; set; }

    private unsafe void DrawHeld(Span<float> frameConstants, RenderStats stats)
    {
        CommandBuffer commandBuffer = _renderer.CommandBuffer;
        if (_heldMesh.IsEmpty)
        {
            return;
        }

        // Konstanty se zkopírují a přepíše se v nich jen matice — mlha, světlo a čas mají
        // zůstat stejné jako ve světě, aby ruka nesvítila jinak než okolí.
        Span<float> heldConstants = stackalloc float[64];
        frameConstants.CopyTo(heldConstants);
        if (HeldViewProjection is { } projection)
        {
            Matrix4 clip = VulkanClip.ToVulkan(projection);
            MemoryMarshal.Cast<Matrix4, float>(MemoryMarshal.CreateSpan(ref clip, 1))
                .CopyTo(heldConstants);
        }

        int slot = _renderer.FrameSlot % ItemBufferSlots;

        _heldBuffers[slot] ??= NewBuffers();
        _heldBuffers[slot]!.Upload(_heldMesh);


        SetDepthRange(0f, HeldDepthRange);

        BindPass(commandBuffer, _items, heldConstants);
        DrawBuffers(commandBuffer, _items, Vector3i.Zero, _heldBuffers[slot]!, stats);

        SetDepthRange(0f, 1f);
    }

    /// <summary>
    /// Kolik nejbližší hloubky si ruka zabere. Zbytek zůstává světu.
    /// </summary>
    /// <remarks>
    /// <b>Ne moc málo.</b> Zmáčknutí do pěti procent sice zaručilo, že ruku nic nezakryje,
    /// ale dvacetkrát to zhoršilo přesnost hloubky uvnitř nástroje samotného — a nástroj
    /// z modelu má stěny blízko u sebe. Třetina rozsahu je pořád mnohem blíž než cokoli ve
    /// světě: nejbližší blok, ke kterému hráč dosáhne, leží kolem třiceti centimetrů,
    /// což je při daném průmětu hloubka přes 0,8.
    /// </remarks>
    private const float HeldDepthRange = 0.3f;

    /// <summary>
    /// Zúží hloubkový rozsah. Ruka se tím vejde před svět, aniž by do něj prorůstala.
    /// </summary>
    private unsafe void SetDepthRange(float min, float max)
    {
        var viewport = new Viewport
        {
            X = 0f,
            Y = 0f,
            Width = _swapchain.Extent.Width,
            Height = _swapchain.Extent.Height,
            MinDepth = min,
            MaxDepth = max,
        };

        _context.Vk.CmdSetViewport(_renderer.CommandBuffer, 0, 1, &viewport);
    }

    /// <summary>Kvádr v soustavě tělesa. Slouží ruce i bloku, liší se jen poloosami.</summary>
    private static void AddHeldBox(
        MeshBuffer mesh, Func<Vector3, Vector3> place, float layer, float hx, float hy, float hz)
    {
        var uv00 = new Vector2(0f, 1f);
        var uv10 = new Vector2(1f, 1f);
        var uv11 = new Vector2(1f, 0f);
        var uv01 = new Vector2(0f, 0f);

        void Quad(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float shade) =>
            mesh.AddQuad(
                place(p0), place(p1), place(p2), place(p3),
                uv00, uv10, uv11, uv01, layer, shade, shade, shade, shade, false);

        float side = FaceShading.ForAxis(0, positive: true);
        float top = FaceShading.ForAxis(1, positive: true);
        float front = FaceShading.ForAxis(2, positive: true);

        Quad(new(-hx, -hy, hz), new(hx, -hy, hz), new(hx, hy, hz), new(-hx, hy, hz), front);
        Quad(new(hx, -hy, -hz), new(-hx, -hy, -hz), new(-hx, hy, -hz), new(hx, hy, -hz), front);
        Quad(new(-hx, -hy, -hz), new(-hx, -hy, hz), new(-hx, hy, hz), new(-hx, hy, -hz), side);
        Quad(new(hx, -hy, hz), new(hx, -hy, -hz), new(hx, hy, -hz), new(hx, hy, hz), side);
        Quad(new(-hx, hy, hz), new(hx, hy, hz), new(hx, hy, -hz), new(-hx, hy, -hz), top);
        Quad(new(-hx, -hy, -hz), new(hx, -hy, -hz), new(hx, -hy, hz), new(-hx, -hy, hz), top * 0.7f);
    }

    /// <summary>
    /// Vykresli skutecnou masku slabu nebo schodu. Vnitrni steny mezi sousednimi
    /// pulbloky se vynechavaji, takze predmet nema blikajici prekryvajici se plochy.
    /// </summary>
    internal static void AddHeldPieces(
        MeshBuffer mesh,
        Func<Vector3, Vector3> place,
        float layer,
        byte pieces,
        float half)
    {
        float Step(int value) => -half + (value * half);
        var uv00 = new Vector2(0f, 1f);
        var uv10 = new Vector2(1f, 1f);
        var uv11 = new Vector2(1f, 0f);
        var uv01 = new Vector2(0f, 0f);

        void Quad(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float shade) =>
            mesh.AddQuad(
                place(p0), place(p1), place(p2), place(p3),
                uv00, uv10, uv11, uv01, layer,
                shade, shade, shade, shade, false);

        float side = FaceShading.ForAxis(0, positive: true);
        float top = FaceShading.ForAxis(1, positive: true);
        float front = FaceShading.ForAxis(2, positive: true);

        for (int y = 0; y < PieceMask.Steps; y++)
        for (int z = 0; z < PieceMask.Steps; z++)
        for (int x = 0; x < PieceMask.Steps; x++)
        {
            if (!PieceMask.Has(pieces, x, y, z)) continue;

            float x0 = Step(x), x1 = Step(x + 1);
            float y0 = Step(y), y1 = Step(y + 1);
            float z0 = Step(z), z1 = Step(z + 1);

            if (z == PieceMask.Steps - 1 || !PieceMask.Has(pieces, x, y, z + 1))
                Quad(new(x0, y0, z1), new(x1, y0, z1), new(x1, y1, z1), new(x0, y1, z1), front);
            if (z == 0 || !PieceMask.Has(pieces, x, y, z - 1))
                Quad(new(x1, y0, z0), new(x0, y0, z0), new(x0, y1, z0), new(x1, y1, z0), front);
            if (x == 0 || !PieceMask.Has(pieces, x - 1, y, z))
                Quad(new(x0, y0, z0), new(x0, y0, z1), new(x0, y1, z1), new(x0, y1, z0), side);
            if (x == PieceMask.Steps - 1 || !PieceMask.Has(pieces, x + 1, y, z))
                Quad(new(x1, y0, z1), new(x1, y0, z0), new(x1, y1, z0), new(x1, y1, z1), side);
            if (y == PieceMask.Steps - 1 || !PieceMask.Has(pieces, x, y + 1, z))
                Quad(new(x0, y1, z1), new(x1, y1, z1), new(x1, y1, z0), new(x0, y1, z0), top);
            if (y == 0 || !PieceMask.Has(pieces, x, y - 1, z))
                Quad(new(x0, y0, z0), new(x1, y0, z0), new(x1, y0, z1), new(x0, y0, z1), top * 0.7f);
        }
    }

    /// <summary>
    /// Hranatá paže 4 × 12 × 4 s vlastním minecraftovým UV rozbalením v dlaždici 16 × 16.
    /// </summary>
    /// <remarks>
    /// <para>Čtyři boky zabírají panely 4 × 12 texelů, obě čela 4 × 4. Díky tomu mají pixely
    /// na dlouhé stěně stejný fyzický poměr v obou osách a kůže není třikrát natažená.</para>
    /// <para>Souřadnice míří na přesné hranice panelů. Poměr UV je proto stejně jako poměr
    /// geometrie přesně 3 : 1 a každý pixel zůstane fyzicky čtvercový.</para>
    /// </remarks>
    /// <summary>
    /// Kam v atlasu sahá paže postavy. Rozbalení paže 4×12×4 je 16×16 texelů — přesně to,
    /// co tahle ruka odjakživa kreslila — takže stačí posunout počátek a měřítko a ruka
    /// v první osobě je tatáž paže, jakou má postava ve třetí. U Minetest Sama je pruh
    /// na `U 40–56, V 16–32` textury 64×32; vytaženo přímo z vah kosti `Arm_Right`.
    /// </summary>
    public Vector2 HandUvMin { get; set; } = Vector2.Zero;

    public Vector2 HandUvSize { get; set; } =
        new(TextureArray.ArtSize / (float)TextureArray.ArtSize, 1f);

    internal static void AddHeldArm(
        MeshBuffer mesh, Func<Vector3, Vector3> place, float layer,
        Vector2 uvMin, Vector2 uvSize)
    {
        const float hx = 0.08f;
        const float hy = 0.24f;
        const float hz = 0.08f;

        Vector2 TexelEdge(int x, int y) => new(
            uvMin.X + (x / (float)TextureArray.ArtSize * uvSize.X),
            uvMin.Y + (y / (float)TextureArray.ArtSize * uvSize.Y));

        void Quad(
            Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3,
            int x, int y, int width, int height, float shade, bool flipV = false)
        {
            // DLAŇ PATŘÍ NA VZDÁLENÝ KONEC. V modelu je +Y rameno, jenže v první osobě míří
            // tahle osa od hráče pryč — bez převrácení skončí u prstů rukáv a paže vypadá
            // obráceně. Otočí se proto svislé UV bočních stěn a prohodí se obě čela.
            int low = flipV ? y : y + height;
            int high = flipV ? y + height : y;
            Vector2 uv00 = TexelEdge(x, low);
            Vector2 uv10 = TexelEdge(x + width, low);
            Vector2 uv11 = TexelEdge(x + width, high);
            Vector2 uv01 = TexelEdge(x, high);

            mesh.AddQuad(
                place(p0), place(p1), place(p2), place(p3),
                uv00, uv10, uv11, uv01, layer,
                shade, shade, shade, shade, false);
        }

        float side = FaceShading.ForAxis(0, positive: true);
        float top = FaceShading.ForAxis(1, positive: true);
        float front = FaceShading.ForAxis(2, positive: true);

        // Spodní pás atlasu: levý bok, předek, pravý bok, zadní strana.
        Quad(new(-hx, -hy, hz), new(hx, -hy, hz), new(hx, hy, hz), new(-hx, hy, hz),
            4, 4, 4, 12, front, flipV: true);
        Quad(new(hx, -hy, -hz), new(-hx, -hy, -hz), new(-hx, hy, -hz), new(hx, hy, -hz),
            12, 4, 4, 12, front, flipV: true);
        Quad(new(-hx, -hy, -hz), new(-hx, -hy, hz), new(-hx, hy, hz), new(-hx, hy, -hz),
            0, 4, 4, 12, side, flipV: true);
        Quad(new(hx, -hy, hz), new(hx, -hy, -hz), new(hx, hy, -hz), new(hx, hy, hz),
            8, 4, 4, 12, side, flipV: true);

        // Čela prohozená: na vzdáleném konci (+Y) je dlaň, u hráče rameno v rukávu.
        Quad(new(-hx, hy, hz), new(hx, hy, hz), new(hx, hy, -hz), new(-hx, hy, -hz),
            8, 0, 4, 4, top);
        Quad(new(-hx, -hy, -hz), new(hx, -hy, -hz), new(hx, -hy, hz), new(-hx, -hy, hz),
            4, 0, 4, 4, top * 0.7f);
    }

    /// <summary>
    /// Předmět jako těleso vytažené z ikony, otočený kolem svislé osy.
    /// </summary>
    /// <remarks>
    /// Nahradilo to dvě zkřížené karty. Z těch byl z každého úhlu papír a z boku dvě čáry —
    /// jediná věc bez objemu v celé hře.
    /// </remarks>
    private static void AddItemModel(
        MeshBuffer mesh, ItemShape shape, Vector3 centre, float spin, float layer,
        float skyLight, float blockLight)
    {
        float cos = MathF.Cos(spin);
        float sin = MathF.Sin(spin);

        // Těleso stojí na zemi, ne půlkou pod ní.
        Vector3 origin = centre + new Vector3(0f, ItemHalf, 0f);

        Vector3 Place(Vector3 local) => origin + new Vector3(
            (local.X * cos) + (local.Z * sin),
            local.Y,
            (local.Z * cos) - (local.X * sin));

        foreach (ItemShape.Face face in shape.Faces)
        {
            float faceLayer = face.Layer >= 0 ? face.Layer : layer;

            mesh.AddQuadLit(
                Place(face.P0), Place(face.P1), Place(face.P2), Place(face.P3),
                face.U0, face.U1, face.U2, face.U3,
                faceLayer,
                face.Shade * skyLight, face.Shade * skyLight,
                face.Shade * skyLight, face.Shade * skyLight,
                blockLight, blockLight, blockLight, blockLight, false);
        }
    }

    /// <summary>Krychlička předmětu, otočená kolem svislé osy.</summary>
    private static void AddItemCube(
        MeshBuffer mesh, Vector3 centre, float spin, float layer,
        float skyLight, float blockLight)
    {
        const float Half = 0.13f;

        float cos = MathF.Cos(spin) * Half;
        float sin = MathF.Sin(spin) * Half;

        // Čtyři svislé hrany otočeného čtverce.
        Vector3 a = centre + new Vector3(-cos + sin, 0f, -sin - cos);
        Vector3 b = centre + new Vector3(cos + sin, 0f, sin - cos);
        Vector3 c = centre + new Vector3(cos - sin, 0f, sin + cos);
        Vector3 d = centre + new Vector3(-cos - sin, 0f, -sin + cos);

        Vector3 up = new(0f, Half * 2f, 0f);

        var uv00 = new Vector2(0f, 1f);
        var uv10 = new Vector2(1f, 1f);
        var uv11 = new Vector2(1f, 0f);
        var uv01 = new Vector2(0f, 0f);

        void Quad(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float shade)
        {
            float litShade = shade * skyLight;
            mesh.AddQuadLit(
                p0, p1, p2, p3, uv00, uv10, uv11, uv01, layer,
                litShade, litShade, litShade, litShade,
                blockLight, blockLight, blockLight, blockLight, false);
        }

        float side = FaceShading.ForAxis(0, positive: true);
        float top = FaceShading.ForAxis(1, positive: true);

        Quad(a, b, b + up, a + up, side);
        Quad(b, c, c + up, b + up, side);
        Quad(c, d, d + up, c + up, side);
        Quad(d, a, a + up, d + up, side);

        Quad(a + up, b + up, c + up, d + up, top);

        // Spodek se kreslí taky. Předmět leží na zemi jen část života — než dopadne, letí,
        // a při pohledu zdola byl bez něj vidět skrz.
        Quad(d, c, b, a, FaceShading.ForAxis(1, positive: false));
    }

    /// <summary>Září kolem světlých míst? Vypnutím odpadne celý průchod, ne jen jeho účinek.</summary>
    public bool Bloom { get; set; } = true;

    /// <summary>
    /// Odkud výš se pixel počítá za světlý, 0 až 1.
    /// </summary>
    /// <remarks>
    /// Naměřeno na dlaždicích: tráva, listí, kámen ani voda se přes 0,68 nedostanou vůbec
    /// (0,0 % texelů), zato písek ano z 99 %. Práh proto leží výš — jinak by zářil celý
    /// břeh a scéna by přes den vypadala jako přes vazelínu.
    /// </remarks>
    public float BloomThreshold { get; set; } = 1.0f;

    /// <summary>Jak silně se záře přičte.</summary>
    public float BloomStrength { get; set; } = 0.40f;

    /// <summary>
    /// Dosah rozmazání v pixelech.
    /// </summary>
    /// <remarks>
    /// Malý schválně. Široká záře se z jasné oblohy rozlije přes siluety věcí před ní —
    /// stébla trávy pak vypadají vyšší, než jsou, protože jim světlo přeteče přes špičku.
    /// Kompaktní záře drží u zdroje a tenhle artefakt nedělá.
    /// </remarks>
    public float BloomRadius { get; set; } = 3.0f;

    /// <summary>Applies display encoding and a filmic highlight roll-off to the completed scene.</summary>
    public bool CinematicColorGrading { get; set; } = true;

    /// <summary>
    /// Násobek jasu před filmovou křivkou.
    /// </summary>
    /// <remarks>
    /// Bylo tu 0,90, tedy o desetinu ztmavený obraz. Spolu se sytostí a kontrastem
    /// nastavenými na jednotku to dávalo dohromady scénu bez šťávy — vybledlá zeleň,
    /// šedá dálka, žádný rozdíl mezi světlem a stínem.
    /// </remarks>
    public float Exposure { get; set; } = 1.05f;

    /// <summary>
    /// Sytost barev.
    /// </summary>
    /// <remarks>
    /// <b>Bylo 1,02, tedy o dvě procenta.</b> To je pod prahem, kdy si toho oko vůbec
    /// všimne — korekce byla zapnutá, ale fakticky nic nedělala. Tráva a listí přitom
    /// tvoří většinu obrazu a bez sytosti splývají do jedné vybledlé zelené.
    /// </remarks>
    public float ColorSaturation { get; set; } = 1.28f;

    /// <summary>
    /// Kontrast, tedy rozdíl mezi světlem a stínem.
    /// </summary>
    /// <remarks>
    /// Bylo 1,03, ze stejného důvodu bez účinku. Zvedá se míň než sytost — voxelový svět
    /// má velké jednolité plochy a příliš tvrdý kontrast v nich vytáhne schody v osvětlení.
    /// </remarks>
    public float ColorContrast { get; set; } = 1.16f;

    /// <summary>
    /// Rozdělení odstínu: jak silně se světla stočí do tepla a stíny do modra.
    /// </summary>
    /// <remarks>
    /// <para>Tohle dělá „filmový" dojem, ne sytost — ta zvedne všechny barvy stejně, kdežto
    /// tohle rozejde světlo a stín od sebe. Nula je vypnuto.</para>
    ///
    /// <para>Bývalo natvrdo 0,55 uvnitř <see cref="DrawColorGrade"/>, takže se dalo změnit
    /// jen překladem. Vlastnost je tu proto, aby šlo ladit za běhu.</para>
    /// </remarks>
    public float SplitTone { get; set; } = 0.55f;

    /// <summary>Luanti-derived full-screen anti-aliasing for edges left after raster MSAA.</summary>
    public bool Fxaa { get; set; } = true;

    /// <summary>Replaces the framebuffer with the graded scene captured immediately before this call.</summary>
    public void DrawColorGrade()
    {
        CommandBuffer commandBuffer = _renderer.CommandBuffer;
        Span<float> constants = stackalloc float[4];
        constants[0] = CinematicColorGrading ? Exposure : 1f;
        constants[1] = CinematicColorGrading ? ColorSaturation : 1f;
        constants[2] = CinematicColorGrading ? ColorContrast : 1f;
        float splitTone = CinematicColorGrading ? MathF.Max(SplitTone, 0f) : 0f;
        constants[3] = (Fxaa ? 1f : -1f) * (1f + splitTone);

        BindPass(commandBuffer, _colorGrade, constants, _colorGradeDescriptor);
        _context.Vk.CmdDraw(commandBuffer, 3, 1, 0, 0);
    }

    /// <summary>
    /// Přičte k hotovému obrazu záři kolem světlých míst.
    /// </summary>
    /// <remarks>
    /// Volá se až po vší geometrii včetně vody, protože se má rozzářit i hladina. Scéna
    /// musí být v tu chvíli už zkopírovaná do samplovatelného obrazu — o to se stará
    /// volající přes <c>VulkanRenderer.CaptureScene</c>.
    /// </remarks>
    public void DrawBloom()
    {
        CommandBuffer commandBuffer = _renderer.CommandBuffer;
        if (!Bloom)
        {
            return;
        }

        Span<float> constants = stackalloc float[4];
        constants[0] = BloomThreshold;
        constants[1] = BloomStrength;
        constants[2] = BloomRadius;
        constants[3] = 0f;

        BindPass(commandBuffer, _bloom, constants, _bloomDescriptor);

        _context.Vk.CmdDraw(commandBuffer, 3, 1, 0, 0);
    }

    /// <summary>
    /// Kreslí se voda plným procedurálním shaderem — odrazy oblohy, Fresnel, trpyt?
    /// </summary>
    /// <remarks>
    /// <para>Výchozí stav je vypnuto: hladina i boky se vezmou z dlaždice v atlasu a jen se
    /// prosvítí. Vypadá to jako v blokových hrách a hlavně je to čitelné — přes odrazivou
    /// hladinu není poznat, co je pod ní.</para>
    ///
    /// <para>Vlnění, podvodní pohled ani kaustiky se tím nevypínají. Odpadá jen odraz
    /// oblohy a vlastní barva povrchu.</para>
    ///
    /// <para><b>Do shaderu se to veze ZNAMÉNKEM ČASU.</b> Push konstanty jsou plné a Vulkan
    /// zaručuje jen 128 bajtů, takže je rozšířit nejde bez rizika, že to na některé kartě
    /// spadne. Čas je vždycky kladný (roste od startu), takže záporná hodnota nikdy nevznikne
    /// omylem a shader si ji přečte jako příznak — <c>abs()</c> mu vrátí původní čas.</para>
    /// </remarks>
    public bool FancyWater { get; set; }

    /// <summary>
    /// Jak rychle voda pohlcuje světlo, na blok hloubky.
    ///
    /// <para><b>Sníženo z 0,17 na 0,065.</b> Původní hodnota byla nastavená na pohled shora
    /// z lodky a při plavání pod hladinou byla katastrofa: dvacet bloků hluboké dno mělo
    /// útlum 97 %, takže se všechno slilo do jedné tmavě modré placky a hloubka nebyla
    /// poznat. Nová hodnota odpovídá čisté mořské vodě — ve dvaceti blocích zbude zhruba
    /// čtvrtina, což je zároveň to, co se pod vodou opravdu vidí.</para>
    ///
    /// <para>Násobí se <b>třemi různými koeficienty pro R, G a B</b> (2,17 / 0,71 / 0,46,
    /// viz <c>AbsorptionRatio</c> v shaderech). Právě ten nepoměr dělá podvodní svět
    /// modrozeleným místo šedým — červená mizí zhruba pětkrát rychleji než modrá.</para>
    ///
    /// <para><b>Zvýšeno z 0,030 přes 0,052 a 0,082 na 0,125.</b> Při 0,030 zbývala ve dvaceti
    /// blocích čtvrtina světla a mělčina působila jako bazén s reflektorem na dně — voda
    /// vypadala skoro jako vzduch se zeleným nádechem. Mezikroky 0,052 a 0,082 šly správným
    /// směrem, ale dno zůstávalo čitelné hlouběji, než je pod vodou věrohodné. Při 0,125
    /// zbyde ve dvaceti blocích zhruba půl procenta a hloubka je opravdu tmavá.</para>
    /// </summary>
    public float WaterAbsorption { get; set; } = 0.125f;

    /// <summary>
    /// Jak rychle voda pohlcuje světlo po <b>dráze paprsku</b>, na blok.
    ///
    /// <para>Řídí, jak daleko je vidět pod hladinou: 0,05 znamená, že v šedesáti blocích
    /// zbyde pět procent. Je to jiné číslo než <see cref="WaterAbsorption"/> schválně —
    /// to řídí, jak dobře je z lodky vidět dno, tohle jak daleko dohlédne potápěč. Fyzikálně
    /// je to táž veličina, ale hra potřebuje obojí ovládat zvlášť.</para>
    /// </summary>
    /// <remarks>
    /// <para><b>Sníženo z 0,028 na 0,009.</b> Při 0,028 se červená složka utlumila na
    /// e-tinu už po <b>šestnácti blocích</b>, takže hladina dál než pár desítek bloků
    /// skončila celá na barvě rozptýlené vody — potápěč měl nad sebou jednolitý modrý
    /// strop bez ohledu na to, jak velké je Snellovo okno. Rozšiřovat okno tedy nepomáhalo,
    /// protože světlo z něj se cestou k oku stejně ztratilo.</para>
    ///
    /// <para>Při 0,009 vychází e-tina pro červenou na 51 bloků a pro modrou na 240, což
    /// odpovídá čisté tropické vodě. Dohled pod hladinou je pak přes sto bloků.</para>
    ///
    /// <para><b>Zvýšeno z 0,009 postupně až na 0,098 a pak sníženo na 0,050.</b> Sto bloků
    /// dohledu pod vodou je fyzikálně obhajitelné pro křišťálovou lagunu, ale ve hře to
    /// znamenalo, že se pod hladinou vidělo stejně daleko jako nad ní — potopení pak nebylo
    /// vůbec cítit jako změna prostředí. Mezikroky 0,020 / 0,040 / 0,062 se ladily podle
    /// snímků až na 0,098.</para>
    ///
    /// <para><b>Proč zpátky dolů.</b> Při 0,098 padla e-tina pro modrou na 22 bloků a scéna
    /// pod hladinou se držela u barvy rozptýlené vody — nad hlavou byla jednolitá modrá
    /// plocha bez struktury. To má jeden nečekaný důsledek: <b>není vidět lom světla</b>,
    /// protože posunout jednolitou barvu vypadá stejně jako ji neposunout. Deformace na
    /// hladině potřebuje, aby přes ni bylo vidět pobřeží nebo slunce. Při 0,050 vyjde e-tina
    /// pro modrou na 43 bloků, což je pořád zřetelně méně než nad vodou.</para>
    ///
    /// <para>Pozor: tohle číslo je <b>jediné</b>, které řídí i útlum ve <c>sky.frag</c>
    /// (složka 31 push konstant), takže se změnou tady tmavne i pohled na hladinu zespodu.
    /// To je záměr — jinak by strop nad potápěčem zůstal svítivý, zatímco okolí ztmavlo.</para>
    /// </remarks>
    public float WaterPathAbsorption { get; set; } = 0.050f;

    /// <summary>Vzdálenost, od které se mlha začne projevovat.</summary>
    ///
    /// <para>Musí sedět na dohled, jinak mlha spolkne terén dřív, než ho ořízne dohled —
    /// a protože se mísí do barvy oblohy, vypadá to, že krajina končí. Při dohledu
    /// 12 chunků (384 bloků) a světě s 800 bloky převýšení to bylo dobře vidět: spodek
    /// vzdálené hory zmizel v mlze a vrchol zůstal, takže hora <b>plavala ve vzduchu</b>.
    /// Původní hodnoty 140 a 340 byly nastavené pro svět vysoký 128 bloků.</para>
    public float FogStart { get; set; } = 288f;

    /// <summary>Vzdálenost, kde terén splyne s oblohou úplně.</summary>
    public float FogEnd { get; set; } = 384f;

    /// <summary>Vykreslí prostorovou vrstvu přízemní a výškové mlhy.</summary>
    public bool VolumetricFog { get; set; } = true;

    public float CloudQuality { get; set; } = 0.6f;

    public float FogQuality { get; set; } = 0.6f;

    /// <summary>
    /// Blíž než tohle se vzdálený terén nekreslí vůbec.
    /// </summary>
    /// <remarks>
    /// Nastavuje se podle toho, kam sahají chunky. Dokud byl LOD jen výškopis schovaný
    /// čtvrt bloku pod plnou geometrií, nebylo to potřeba — s lesem ano: koruny trčí nad
    /// terén a objevovaly se hráči přímo před nosem, a vykopanou jamou byl místo díry
    /// vidět LOD.
    /// </remarks>
    public float FarNearCutoff { get; set; }

    /// <summary>
    /// Blízký řez LOD rostlin. Na rozdíl od neprůhledného terénu nemá bezpečný překryv:
    /// křížené plochy korun by z děr mezi listy vyčnívaly mezi skutečné stromy.
    /// </summary>
    public float FarPlantCutoff { get; set; }

    /// <summary>
    /// Blíž než tohle se nekreslí <b>hladina</b> vzdáleného terénu. Je to větší číslo než
    /// <see cref="FarNearCutoff"/>.
    /// </summary>
    /// <remarks>
    /// <para><b>Voda potřebuje jinou mez než terén, a to je zásadní rozdíl.</b> LOD zaskakuje
    /// o 128 bloků dovnitř pod chunky, aby při streamingu nevznikaly díry. U neprůhledného
    /// terénu je ten překryv neškodný — dlaždice leží o čtvrt bloku níž a hloubkový test ji
    /// zahodí.</para>
    ///
    /// <para>Hladina se ale <b>míchá</b> a do hloubky nezapisuje. V překryvu se proto
    /// vykreslila dvakrát, chunková i LOD, a dvojité míchání udělalo přes moře světlý pás
    /// přesně na hranici. Vodní mez se proto posouvá až tam, kam sahá plná geometrie.</para>
    /// </remarks>
    public float FarWaterCutoff { get; set; }

    /// <summary>
    /// Nahraje dlaždici vzdáleného terénu. Souřadnice vrcholů jsou už světové, takže se
    /// kreslí s nulovým posunem chunku.
    /// </summary>
    public void UploadFar(
        FarTerrain.TileKey key, MeshBuffer mesh, MeshBuffer water, MeshBuffer plants, Vector3 min, Vector3 max)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(water);
        ArgumentNullException.ThrowIfNull(plants);

        RemoveFar(key);

        if (mesh.IsEmpty && water.IsEmpty && plants.IsEmpty)
        {
            return;
        }

        GpuBuffers buffers = RentFarBuffers();
        GpuBuffers waterBuffers = RentFarBuffers();
        GpuBuffers plantBuffers = RentFarBuffers();

        var tile = new FarTile(buffers, waterBuffers, plantBuffers, new Aabb(min, max));
        tile.Buffers.Upload(mesh);
        tile.Water.Upload(water);
        tile.Plants.Upload(plants);
        _far[key] = tile;
    }

    /// <summary>
    /// Zahodí dlaždici vzdáleného terénu.
    ///
    /// <para>Buffery se <b>neuvolní hned</b>, ale odloží stejnou frontou jako chunky.
    /// GPU z nich může ještě kreslit rozpracovaný snímek; první verze je rušila okamžitě
    /// a skončilo to <c>ErrorDeviceLost</c>.</para>
    /// </summary>
    public void RemoveFar(FarTerrain.TileKey key)
    {
        if (_far.Remove(key, out FarTile existing))
        {
            _retiringFar.Enqueue((existing.Buffers, _renderer.FrameCount));
            _retiringFar.Enqueue((existing.Water, _renderer.FrameCount));
            _retiringFar.Enqueue((existing.Plants, _renderer.FrameCount));
        }
    }

    /// <summary>Nahraje geometrii chunku na GPU. Volá se z hlavního vlákna.</summary>
    public void Upload(
        Vector3i chunkPosition, MeshBuffer opaque, MeshBuffer transparent, MeshBuffer cutout, MeshBuffer micro,
        MeshBuffer water)
    {
        ArgumentNullException.ThrowIfNull(opaque);
        ArgumentNullException.ThrowIfNull(transparent);
        ArgumentNullException.ThrowIfNull(cutout);
        ArgumentNullException.ThrowIfNull(micro);
        ArgumentNullException.ThrowIfNull(water);

        if (opaque.IsEmpty && transparent.IsEmpty && cutout.IsEmpty && micro.IsEmpty && water.IsEmpty)
        {
            Remove(chunkPosition);
            return;
        }

        // Když chunk už geometrii má, jeho buffery se NEPŘEPÍŠOU: GPU z nich může ještě
        // kreslit rozpracovaný snímek. Staré se odloží a vezmou se jiné. V OpenGL to
        // nevadilo, protože zápis do bufferu tam ovladač zařadil za rozdělanou práci sám.
        if (_chunks.Remove(chunkPosition, out GpuChunk? previous))
        {
            Retire(previous);
        }

        GpuChunk gpu = Rent();
        gpu.Upload(opaque, transparent, cutout, micro, water);
        _chunks[chunkPosition] = gpu;

    }

    /// <summary>Odloží prostředky chunku, který vypadl z dohledu, k pozdějšímu použití.</summary>
    public void Remove(Vector3i chunkPosition)
    {
        if (_chunks.Remove(chunkPosition, out GpuChunk? gpu))
        {
            Retire(gpu);
        }
    }

    private void Retire(GpuChunk gpu)
    {
        gpu.Retire();
        _retiring.Enqueue((gpu, _renderer.FrameCount));
    }

    /// <summary>Založí buffery napojené na sdílené místo. Vlastní GL buffer už nemají.</summary>
    private GpuBuffers NewBuffers() => new(_context);

    private GpuChunk Rent()
    {
        ReclaimRetired();
        if (_free.Count > 0)
        {
            GpuChunk reused = _free.Dequeue();
            _pooledBytes -= reused.ByteSize;
            return reused;
        }

        return new GpuChunk(_context);
    }

    /// <summary>
    /// Vrátí do zásoby ty odložené chunky, jejichž snímky už doběhly. Dokud neuplyne
    /// <c>FramesInFlight</c> snímků, může z nich GPU pořád číst.
    /// </summary>
    /// <summary>Půjčí buffery dlaždice ze zásoby, nebo založí nové.</summary>
    private GpuBuffers RentFarBuffers()
    {
        if (_freeFar.Count > 0)
        {
            GpuBuffers reused = _freeFar.Dequeue();
            _pooledFarBytes -= reused.ByteSize;
            return reused;
        }

        return NewBuffers();
    }

    private void ReclaimRetired()
    {
        while (_retiringFar.TryPeek(out (GpuBuffers Buffers, long Frame) far)
               && _renderer.FrameCount - far.Frame > VulkanRenderer.FramesInFlight)
        {
            _retiringFar.Dequeue();

            // DLAŽDICE LOD JSOU ŘÁDOVĚ VĚTŠÍ NEŽ CHUNKY, takže jich v zásobě smí být míň.
            // Pokrývají 64×64 buněk, tedy kus světa velký jako spousta chunků najednou;
            // se stejným stropem jako chunky by samotná tahle zásoba držela stovky megabajtů.
            if (_freeFar.Count < MaxPooledTiles && _pooledFarBytes + far.Buffers.ByteSize <= MaxPooledBytes)
            {
                _pooledFarBytes += far.Buffers.ByteSize;
                far.Buffers.Retire();
                _freeFar.Enqueue(far.Buffers);
            }
            else
            {
                far.Buffers.Dispose();
            }
        }

        while (_retiring.Count > 0)
        {
            (GpuChunk chunk, long frame) = _retiring.Peek();

            if (_renderer.FrameCount - frame < VulkanRenderer.FramesInFlight)
            {
                break;
            }

            _retiring.Dequeue();

            if (_free.Count < MaxPooledChunks && _pooledBytes + chunk.ByteSize <= MaxPooledBytes)
            {
                _pooledBytes += chunk.ByteSize;
                _free.Enqueue(chunk);
            }
            else
            {
                chunk.Dispose();
            }
        }
    }

    public void Draw(
        Matrix4 viewProjection, Vector3 cameraPosition, float nearPlane, float farPlane,
        Frustum frustum, RenderStats stats)
    {
        CommandBuffer commandBuffer = _renderer.CommandBuffer;
        ArgumentNullException.ThrowIfNull(frustum);
        ArgumentNullException.ThrowIfNull(stats);

        ReclaimRetired();


        // Do shaderu jde matice převedená do konvence Vulkanu; frustum výš pracuje dál
        // s tou v konvenci OpenGL, protože z ní vytahuje roviny.
        Matrix4 clip = VulkanClip.ToVulkan(viewProjection);

        Span<float> frameConstants = stackalloc float[64];
        MemoryMarshal.Cast<Matrix4, float>(MemoryMarshal.CreateSpan(ref clip, 1)).CopyTo(frameConstants);

        frameConstants[16] = cameraPosition.X;
        frameConstants[17] = cameraPosition.Y;
        frameConstants[18] = cameraPosition.Z;
        frameConstants[19] = FogStart;

        frameConstants[20] = FogColor.X;
        frameConstants[21] = FogColor.Y;
        frameConstants[22] = FogColor.Z;
        frameConstants[23] = FogEnd;

        frameConstants[24] = SeaLevel;
        frameConstants[25] = WaterAbsorption;
        frameConstants[26] = WaterPathAbsorption;
        // Znaménko nese volbu vzhledu vody, viz FancyWater.
        frameConstants[27] = FancyWater ? Time : -Time;

        frameConstants[32..59].Clear();

        // Kolik denního světla dopadá. V noci se tím ztlumí celý svět a barevný nádech
        // se překlopí do měsíčního modra.
        frameConstants[59] = Day.Daylight;

        Vector3 light = Day.LightColor();
        frameConstants[60] = light.X;
        frameConstants[61] = light.Y;
        frameConstants[62] = light.Z;
        frameConstants[63] = Day.Moonlight;

        _gpu.BeginFrame(commandBuffer);

        // Obloha se kreslí PRVNÍ a bez hloubky, takže vyplní obraz a terén se pak
        // normálně vykreslí přes ni. Nahrazuje mazací barvu, která byla plochá.
        DrawSky(clip, cameraPosition, nearPlane, farPlane, stats);

        _gpu.Mark(commandBuffer);

        VulkanPipeline opaquePipeline = BackfaceCulling ? _opaque : _opaqueNoCull;
        VulkanPipeline transparentPipeline = BackfaceCulling ? _transparent : _transparentNoCull;

        VisibleChunks = 0;
        _visible.Clear();

        BindPass(commandBuffer, opaquePipeline, frameConstants);

        foreach ((Vector3i position, GpuChunk gpu) in _chunks)
        {
            if (!frustum.Intersects(VoxelWorld.ChunkBounds(position)))
            {
                continue;
            }

            VisibleChunks++;
            _visible.Add(position);
            DrawBuffers(commandBuffer, opaquePipeline, position, gpu.Opaque, stats);
        }

        _gpu.Mark(commandBuffer);

        // Vzdálený terén má VLASTNÍ pipeline, protože jeho shader musí umět jednu věc
        // navíc: zahodit fragmenty blíž než dosah chunků.
        //
        // Dlaždice se kreslí celá, i když jen kus z ní přesahuje za chunky — bližší část
        // tedy leží mezi nimi. Řezat to při stavbě nejde, protože dlaždice se postaví
        // jednou a hráč se k ní pak přiblíží; řezat celé dlaždice při kreslení taky ne,
        // protože hranici přecházejí. Fragment svou vzdálenost zná přesně a každý snímek
        // znovu, takže řez sedne vždycky.
        VisibleFarTiles = 0;

        if (_far.Count > 0)
        {
            BindPass(commandBuffer, _farPipeline, frameConstants);

            foreach (FarTile tile in _far.Values)
            {
                if (tile.Buffers.IndexCount == 0 || !frustum.Intersects(tile.Bounds))
                {
                    continue;
                }

                VisibleFarTiles++;
                DrawBuffers(commandBuffer, _farPipeline, Vector3i.Zero, tile.Buffers, stats, FarNearCutoff);
            }

            _gpu.Mark(commandBuffer);

            // Rostliny vzdáleného terénu hned za jeho povrchem: mají alfa test a zápis do
            // hloubky, takže musí ven dřív než cokoli průhledného.
            BindPass(commandBuffer, _farPlants, frameConstants);

            foreach (FarTile tile in _far.Values)
            {
                if (tile.Plants.IndexCount == 0 || !frustum.Intersects(tile.Bounds))
                {
                    continue;
                }

                DrawBuffers(commandBuffer, _farPlants, Vector3i.Zero, tile.Plants, stats, FarPlantCutoff);
            }

            // Hladina vzdáleného terénu se kreslí až dole u vody, ne tady. Potřebuje totiž
            // scénu zkopírovanou stranou stejně jako voda blízká, a ta vzniká až po vší
            // neprůhledné geometrii.

            // Zpátky na neprůhledný pipeline kvůli mikro průchodu níž.
            BindPass(commandBuffer, opaquePipeline, frameConstants);
        }

        // Značky se zapisují i když se vzdálený terén nekreslil — jinak by se úseky
        // posunuly a čísla by patřila k jinému průchodu.
        if (_far.Count == 0)
        {
            _gpu.Mark(commandBuffer);
        }

        _gpu.Mark(commandBuffer);

        // Mikro průchod: samostatný buffer, ale jinak stejný stav jako neprůhledná geometrie,
        // takže se používá tentýž pipeline a nemusí se znovu navazovat.
        foreach (Vector3i position in _visible)
        {
            DrawBuffers(commandBuffer, opaquePipeline, position, _chunks[position].Micro, stats);
        }

        // Kabely jsou pevna nepruhledna geometrie a musi zapsat hloubku pred rostlinami,
        // pruhlednymi bloky a zachycenim sceny pro vodu.
        DrawPower(opaquePipeline, frameConstants, stats);

        // Dveře a trapdoory během krátkého otočení nejsou ve statické meshi chunku.
        DrawAnimatedBuildingParts(frameConstants, stats);

        // Vanilla fauna is opaque dynamic geometry. It must be captured by water reflections,
        // so it is rendered before the scene is split for the water pass.
        DrawAnimals(frameConstants, stats);

        _gpu.Mark(commandBuffer);

        // Výřezový průchod: rostliny. Kreslí se PŘED průhledným a se zápisem do hloubky,
        // takže se stébla navzájem zakryjí — v louce jinak vychází stovky ploch přes sebe
        BindPass(commandBuffer, _cutout, frameConstants);

        foreach (Vector3i position in _visible)
        {
            DrawBuffers(commandBuffer, _cutout, position, _chunks[position].Cutout, stats);
        }

        _gpu.Mark(commandBuffer);

        // Průhledný průchod: míchání zapnuté, do hloubky se nezapisuje, aby se skla
        // navzájem neořezávala. Bez řazení odzadu dopředu — průhledných ploch je pár.
        BindPass(commandBuffer, transparentPipeline, frameConstants);

        foreach (Vector3i position in _visible)
        {
            DrawBuffers(commandBuffer, transparentPipeline, position, _chunks[position].Transparent, stats);
        }

        // LEŽÍCÍ PŘEDMĚTY A RUKA JEŠTĚ PŘED ROZDĚLENÍM SNÍMKU.
        //
        // Jsou to výřezové plochy, které zapisují do hloubky — a od rozdělení je hloubka
        // připojená jen ke čtení. Dokud se kreslily až za ním, porušovalo to specifikaci:
        // validační vrstva hlásila „depthWriteEnable is VK_TRUE, but pDepthAttachment is
        // read-only". Nebylo to vidět, protože se to dělo jen ve snímcích, kde zrovna něco
        // leželo na zemi; s rukou, která je v obraze pořád, z toho byla chyba na každý
        // snímek a tím se to našlo.
        //
        // Vedlejší zisk: předměty jsou teď i v kopii scény pro vodu, takže se odrážejí
        // a lámou stejně jako terén.
        if (DropItems is not null)
        {
            DrawItems(Drops ?? [], DropItems, frameConstants, stats);
        }

        // INSTANCOVANÉ ITEMY. Kreslí se tady, protože patří mezi neprůhlednou geometrii —
        // musí být v kopii scény pro vodu a musí zapisovat do hloubky.
        DrawInstancedItems(commandBuffer, frameConstants, stats);

        // ROZDĚLENÍ SNÍMKU. Až sem je hotová všechna neprůhledná geometrie, takže se obraz
        // zkopíruje stranou a hloubka se přepne na čtení. Od téhle chvíle si voda umí
        // sáhnout na to, co je za ní a pod ní — bez toho by odrážela jen analytickou
        // oblohu a břeh ani kopce by v ní vidět nebyly.
        _gpu.Mark(commandBuffer);

        _renderer.CaptureSceneForWater();

        _gpu.Mark(commandBuffer);

        // Praskliny až za vší pevnou geometrií: míchají se přes hotový blok, takže musí
        // být to poslední, co se přes něj kreslí.
        DrawBreaking(frameConstants, stats);

        // Voda až úplně nakonec. Hladina je průhledná a musí se míchat přes hotové dno —
        // kdyby šla dřív, mísila by se s tím, co ještě nebylo nakresleno.
        BindPass(commandBuffer, _water, frameConstants, _waterDescriptor);

        foreach (Vector3i position in _visible)
        {
            DrawBuffers(commandBuffer, _water, position, _chunks[position].Water, stats);
        }

        _gpu.Mark(commandBuffer);

        // Hladina vzdáleného terénu. Týž shader, ale shader si ji podle nenulového
        // nearCutoff pozná a kreslí ji neprůhledně — pod LOD dlaždicemi žádné dno není.
        if (_far.Count > 0)
        {
            BindPass(commandBuffer, _farWater, frameConstants, _waterDescriptor);

            foreach (FarTile tile in _far.Values)
            {
                if (tile.Water.IndexCount == 0 || !frustum.Intersects(tile.Bounds))
                {
                    continue;
                }

                DrawBuffers(commandBuffer, _farWater, Vector3i.Zero, tile.Water, stats, FarWaterCutoff);
            }
        }

        // Poslední značka uzavírá poslední úsek.
        // Fog belongs above water so the low layer remains visible over lakes, while the
        // high cloud volume stays the final atmospheric pass.
        if (VolumetricFog)
        {
            DrawSky(
                clip, cameraPosition, nearPlane, farPlane, stats,
                volumetricFog: true);
        }
        DrawSky(
            clip, cameraPosition, nearPlane, farPlane, stats,
            cloudOverlay: true);

        _gpu.Mark(commandBuffer);
    }

    /// <summary>
    /// Vykreslí oblohu jedním fullscreen trojúhelníkem.
    /// </summary>
    /// <remarks>
    /// <para><b>Nahrazuje mazací barvu.</b> Nebe bylo do téhle chvíle jediná plochá barva
    ///
    /// <para><b>Směr paprsku se skládá z INVERZNÍ projekce, ne z os kamery.</b> Osy by byly
    /// o pár instrukcí levnější, ale musely by se do rendereru dotáhnout jako další čtyři
    /// vlastnosti a držet v souladu s maticí. Jedna inverze 4×4 za snímek je proti tomu
    /// nic a nemůže se rozejít s tím, čím se kreslí terén.</para>
    ///
    /// <para>Vrcholy se nenačítají z paměti — trojúhelník si shader vyrobí z
    /// <c>gl_VertexIndex</c>. Proto má pipeline prázdný popis vrcholu.</para>
    /// </remarks>
    private void DrawSky(
        Matrix4 clip, Vector3 cameraPosition,
        float nearPlane, float farPlane, RenderStats stats,
        bool cloudOverlay = false, bool volumetricFog = false)
    {
        CommandBuffer commandBuffer = _renderer.CommandBuffer;

        Span<float> constants = stackalloc float[SkyConstantFloats];

        // OSY KAMERY MÍSTO INVERZNÍ MATICE.
        //
        // Obloha si dřív směr paprsku dopočítávala tak, že převedla bod na vzdálené rovině
        // zpátky do světa <b>inverzí</b> matice pohledu. Terén se přitom kreslí maticí
        // přímou — a právě v tom rozdílu byla chyba, kvůli které terén stál a slunce při
        // otáčení myší poskakovalo.
        //
        // Poměr blízké a vzdálené roviny je 0,12 : 8192, takže je ta matice na inverzi
        // špatně podmíněná. Naměřeno ve <c>float</c>, tedy v přesnosti shaderu: paprsek
        // z inverze míří vedle až o 0,55 pixelu a při rovnoměrném otáčení kolísá jeho krok
        // o 0,41 px, přičemž samotný krok je 0,376 px. <b>Kolísání bylo větší než pohyb.</b>
        //
        // Osy kamery jsou jednotkové vektory a rozevření je jedno číslo, takže se paprsek
        // složí ze tří dobře podmíněných hodnot bez jediného odčítání velkých čísel. Viz
        // SkyRayTests.
        WriteVector(constants[0..3], SkyRay.Right);
        WriteVector(constants[4..7], SkyRay.Up);
        WriteVector(constants[8..11], SkyRay.Forward);
        constants[11] = Math.Clamp(volumetricFog ? FogQuality : CloudQuality, 0f, 1f);

        // The sky and cloud shaders do not use these W components. Fog uses them to
        // reconstruct linear world distance from the non-linear Vulkan depth buffer.
        constants[3] = MathF.Max(nearPlane, 0.001f);
        constants[7] = MathF.Max(farPlane, constants[3] + 1f);

        constants[12] = cameraPosition.X;
        constants[13] = cameraPosition.Y;
        constants[14] = cameraPosition.Z;
        constants[15] = Time;

        // SMĚR SLUNCE UŽ NENÍ KONSTANTA. Obloha ho dostávala jako push konstantu už dřív,
        // takže stačilo přestat ho brát ze statické tabulky — kreslení se nemuselo dotknout.
        WriteVector(constants[16..19], Day.SunDirection);
        constants[19] = SeaLevel;

        WriteVector(constants[20..23], Day.Zenith());

        // Náběh přes 35 cm, ne skok. S ostrým přepnutím na hladině obraz při každém
        // nadechnutí bliknul, protože se celá obloha změnila mezi dvěma snímky.
        constants[23] = Math.Clamp((SeaLevel - cameraPosition.Y) / 0.35f, 0f, 1f);

        WriteVector(constants[24..27], Day.Horizon());

        // Poslední volná složka v celém bloku. Shader oblohy z ní počítá útlum na dráze
        // od oka k hladině, takže musí sedět na tom, čím počítá útlum terén.
        constants[27] = WaterPathAbsorption;

        VulkanPipeline pipeline = volumetricFog
            ? _volumetricFog
            : cloudOverlay ? _cloudOverlay : _sky;
        VulkanTextureSet descriptor = volumetricFog
            ? _waterDescriptor
            : cloudOverlay ? _cloudOverlayDescriptor : _skyDescriptor;

        BindPass(commandBuffer, pipeline, constants, descriptor);

        _context.Vk.CmdDraw(commandBuffer, 3, 1, 0, 0);
        stats.CountDrawCall();
    }

    /// <param name="descriptor">
    /// Sada textur. Výchozí je atlas bloků; voda si předá vlastní se scénou a hloubkou.
    /// </param>
    private unsafe void BindPass(
        CommandBuffer commandBuffer, VulkanPipeline pipeline, ReadOnlySpan<float> frameConstants,
        VulkanTextureSet? descriptor = null)
    {
        Vk vk = _context.Vk;

        vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, pipeline.Handle);
        (descriptor ?? _descriptorA).Bind(commandBuffer, pipeline.Layout);

        // Per-frame část push konstant se posílá jednou na průchod. Posun chunku
        // (16 B na offsetu ChunkOffsetOffset) se dopisuje u každého draw callu zvlášť.
        //
        // POSÍLÁ SE JEN TOLIK, KOLIK VOLAJÍCÍ OPRAVDU DAL. Stínový průchod sem předává
        // samotnou matici, tedy 64 B; posílat napevno 112 by četlo za koncem pole.
        uint pushBytes = Math.Min(ChunkOffsetOffset, (uint)frameConstants.Length * sizeof(float));

        fixed (float* constantsPtr = frameConstants)
        {
            vk.CmdPushConstants(
                commandBuffer, pipeline.Layout,
                ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit,
                0, pushBytes, constantsPtr);
        }

        // ZA POSUNEM CHUNKU JEŠTĚ SVĚTLO. Matice slunce a síla stínu leží až za ním, takže
        // je první zápis nezachytí — a bez druhého by se do shaderu nikdy nedostaly.
        if (frameConstants.Length >= LightConstantFloats && pipeline.PushConstantBytes >= PushConstantSize)
        {
            fixed (float* lightPtr = frameConstants[LightConstantOffset..])
            {
                vk.CmdPushConstants(
                    commandBuffer, pipeline.Layout,
                    ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit,
                    LightConstantOffset * sizeof(float), LightConstantSize * sizeof(float), lightPtr);
            }
        }
    }

    /// <param name="nearCutoff">
    /// Vzdálenost, blíž k níž se nic nekreslí. Používá jen vzdálený terén; chunky mají nulu.
    /// </param>
    private unsafe void DrawBuffers(
        CommandBuffer commandBuffer, VulkanPipeline pipeline, Vector3i position, GpuBuffers buffers,
        RenderStats stats, float nearCutoff = 0f)
    {
        if (buffers.IndexCount == 0)
        {
            return;
        }

        Vk vk = _context.Vk;

        Vector3 origin = ChunkOrigin(position);
        Span<float> chunkOffset = stackalloc float[4] { origin.X, origin.Y, origin.Z, nearCutoff };

        fixed (float* offsetPtr = chunkOffset)
        {
            vk.CmdPushConstants(
                commandBuffer, pipeline.Layout,
                ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit,
                ChunkOffsetOffset, 4 * sizeof(float), offsetPtr);
        }

        Silk.NET.Vulkan.Buffer vertexBuffer = buffers.VertexBuffer!.Handle;
        ulong offset = 0;
        vk.CmdBindVertexBuffers(commandBuffer, 0, 1, &vertexBuffer, &offset);
        vk.CmdBindIndexBuffer(commandBuffer, buffers.IndexBuffer!.Handle, 0, IndexType.Uint32);

        vk.CmdDrawIndexed(commandBuffer, (uint)buffers.IndexCount, 1, 0, 0, 0);
        stats.CountDrawCall();
    }

    public void Dispose()
    {
        foreach (GpuChunk gpu in _chunks.Values)
        {
            gpu.Dispose();
        }

        while (_retiring.Count > 0)
        {
            _retiring.Dequeue().Chunk.Dispose();
        }

        foreach (FarTile tile in _far.Values)
        {
            tile.Buffers.Dispose();
            tile.Water.Dispose();
            tile.Plants.Dispose();
        }

        _far.Clear();

        while (_retiringFar.TryDequeue(out (GpuBuffers Buffers, long Frame) far))
        {
            far.Buffers.Dispose();
        }

        while (_freeFar.TryDequeue(out GpuBuffers? spare))
        {
            spare.Dispose();
        }

        foreach (GpuChunk gpu in _free)
        {
            gpu.Dispose();
        }

        _chunks.Clear();
        _free.Clear();

        _gpu.Dispose();
        _instancedItemsDescriptor.Dispose();
        _instancedItems.Dispose();
        _colorGrade.Dispose();
        _bloom.Dispose();
        _opaque.Dispose();
        _transparent.Dispose();
        _opaqueNoCull.Dispose();
        _transparentNoCull.Dispose();
        _farPipeline.Dispose();
        _farPlants.Dispose();
        _cutout.Dispose();
        _items.Dispose();
        foreach (GpuBuffers? buffers in _itemBuffers)
        {
            buffers?.Dispose();
        }
        foreach (GpuBuffers? buffers in _animalBuffers)
        {
            buffers?.Dispose();
        }

        foreach (GpuBuffers? buffers in _heldBuffers)
        {
            buffers?.Dispose();
        }

        foreach (GpuBuffers? buffers in _powerBuffers)
        {
            buffers?.Dispose();
        }

        foreach (GpuBuffers? buffers in _buildingPartBuffers)
        {
            buffers?.Dispose();
        }
        _crackBuffers?.Dispose();
        _water.Dispose();
        _farWater.Dispose();
        _sky.Dispose();
        _cloudOverlay.Dispose();
        _volumetricFog.Dispose();
    }

    private static Vector3 ChunkOrigin(Vector3i position) =>
        new(position.X * Chunk.Size, position.Y * Chunk.Size, position.Z * Chunk.Size);

    /// <summary>Prostředky jednoho chunku na GPU.</summary>
    /// <summary>Nahraná dlaždice vzdáleného terénu i s obalem pro frustum culling.</summary>
    private readonly record struct FarTile(
        GpuBuffers Buffers, GpuBuffers Water, GpuBuffers Plants, Aabb Bounds);

    private sealed class GpuChunk : IDisposable
    {
        public GpuChunk(VulkanContext context)
        {
            Opaque = new GpuBuffers(context);
            Transparent = new GpuBuffers(context);
            Cutout = new GpuBuffers(context);
            Micro = new GpuBuffers(context);
            Water = new GpuBuffers(context);
        }

        /// <summary>Kolik VRAM tenhle chunk drží, včetně nevyužité kapacity.</summary>
        public long ByteSize =>
            Opaque.ByteSize + Transparent.ByteSize + Cutout.ByteSize + Micro.ByteSize + Water.ByteSize;

        public GpuBuffers Opaque { get; }

        public GpuBuffers Transparent { get; }

        /// <summary>
        /// Rostliny. Vlastní buffer, protože se kreslí se zápisem do hloubky a bez
        /// míchání — na rozdíl od skla, které míchání opravdu potřebuje.
        /// </summary>
        public GpuBuffers Cutout { get; }

        /// <summary>
        /// Geometrie otesaných bloků. Má vlastní buffer, protože vzniká jinou cestou —
        /// ze sdílených tvarů — a do chunk meshe se nikdy nemíchá.
        /// </summary>
        public GpuBuffers Micro { get; }

        /// <summary>
        /// Hladina a stěny vody. Vlastní buffer, protože potřebuje vlastní shader —
        /// odraz oblohy a odlesk slunce závisí na směru pohledu a do vrcholu se zapéct
        /// nedají. Viz parametr <c>water</c> u <see cref="ChunkMesher.Build"/>.
        /// </summary>
        public GpuBuffers Water { get; }

        public void Upload(
            MeshBuffer opaque, MeshBuffer transparent, MeshBuffer cutout, MeshBuffer micro, MeshBuffer water)
        {
            Opaque.Upload(opaque);
            Transparent.Upload(transparent);
            Cutout.Upload(cutout);
            Micro.Upload(micro);
            Water.Upload(water);
        }

        /// <summary>Odloží chunk k pozdějšímu použití: zapomene obsah, ale buffery si nechá.</summary>
        public void Retire()
        {
            Opaque.Retire();
            Transparent.Retire();
            Cutout.Retire();
            Micro.Retire();
            Water.Retire();
        }

        public void Dispose()
        {
            Opaque.Dispose();
            Transparent.Dispose();
            Cutout.Dispose();
            Micro.Dispose();
            Water.Dispose();
        }
    }

    /// <summary>
    /// Dvojice buffer vrcholů + buffer indexů.
    ///
    /// Buffery se zakládají s rezervou; recyklovaný se pak mnohem častěji trefí do
    /// stávající kapacity a nemusí se sahat na ovladač. Na Apple Silicon je paměť sdílená,
    /// takže se do nich zapisuje přímo a staging buffer není potřeba.
    /// </summary>
    /// <summary>
    /// Dvojice buffer vrcholů + buffer indexů jednoho chunku.
    ///
    /// Buffery se zakládají s rezervou podle velikostních tříd; recyklovaný chunk se pak
    /// mnohem častěji trefí do stávající kapacity a nemusí se alokovat paměť zařízení.
    /// </summary>
    private sealed class GpuBuffers : IDisposable
    {
        private readonly VulkanContext _context;

        public GpuBuffers(VulkanContext context) => _context = context;

        public VulkanBuffer? VertexBuffer { get; private set; }

        public VulkanBuffer? IndexBuffer { get; private set; }

        public int IndexCount { get; private set; }

        public void Upload(MeshBuffer source)
        {
            IndexCount = source.IndexCount;
            if (IndexCount == 0)
            {
                return;
            }

            int vertexBytes = source.VertexCount * Stride;
            int indexBytes = source.IndexCount * sizeof(uint);

            if (VertexBuffer is null || VertexBuffer.Capacity < (ulong)vertexBytes)
            {
                VertexBuffer?.Dispose();
                VertexBuffer = VulkanBuffer.CreateHostVisible(
                    _context, SizeClass(vertexBytes), BufferUsageFlags.VertexBufferBit);
            }

            if (IndexBuffer is null || IndexBuffer.Capacity < (ulong)indexBytes)
            {
                IndexBuffer?.Dispose();
                IndexBuffer = VulkanBuffer.CreateHostVisible(
                    _context, SizeClass(indexBytes), BufferUsageFlags.IndexBufferBit);
            }

            VertexBuffer.Write<float>(
                source.RawVertices.AsSpan(0, source.VertexCount * MeshBuffer.FloatsPerVertex));
            IndexBuffer.Write<uint>(source.RawIndices.AsSpan(0, source.IndexCount));
        }

        /// <summary>
        /// Zaokrouhlí velikost bufferu na velikostní třídu.
        ///
        /// <para>Buffery se předtím zakládaly na přesnou velikost s rezervou 1,5×. Recyklovaný
        /// chunk pak skoro nikdy neměl buffer, do kterého by se nová mesh vešla, takže se
        /// pořád zakládaly nové — a <c>vkAllocateMemory</c> umí zastavit hlavní vlákno.
        /// Naměřeno: nejhorší nahrání <b>21 ms</b>, tedy víc než celý rozpočet na frame.</para>
        ///
        /// <para>Mocniny dvou plýtvaly: naměřeno 1209 MB paměti zařízení při 5403 bufferech.
        /// Zaokrouhlení na násobek drží plýtvání shora omezené a recyklace se pořád trefuje.</para>
        /// </summary>
        private static ulong SizeClass(int bytes)
        {
            const ulong SmallStep = 32 * 1024;
            const ulong LargeStep = 256 * 1024;
            const ulong SmallLimit = 256 * 1024;

            ulong needed = (ulong)Math.Max(bytes, 1);
            ulong step = needed <= SmallLimit ? SmallStep : LargeStep;

            return ((needed + step - 1) / step) * step;
        }

        /// <summary>Kolik VRAM buffery drží. Počítá se KAPACITA, ne obsah.</summary>
        public long ByteSize => (long)((VertexBuffer?.Capacity ?? 0) + (IndexBuffer?.Capacity ?? 0));

        /// <summary>Zapomene obsah, ale buffery si nechá pro další chunk.</summary>
        public void Retire() => IndexCount = 0;

        public void Dispose()
        {
            VertexBuffer?.Dispose();
            VertexBuffer = null;

            IndexBuffer?.Dispose();
            IndexBuffer = null;

            IndexCount = 0;
        }
    }
}
