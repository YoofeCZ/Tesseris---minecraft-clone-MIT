using OpenTK.Mathematics;
using Silk.NET.Vulkan;

// System.Threading.Semaphore se jmenuje stejne jako ten vulkansky. Alias urcuje, ktery se mysli.
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace Tesseris.Engine.Rendering.Vulkan;

/// <summary>
/// Smyčka snímku: získání obrazu ze swapchainu, nahrávání příkazů, odeslání a zobrazení.
/// Nad tím už staví <c>ChunkRenderer</c> a <c>TextRenderer</c>.
/// </summary>
public sealed unsafe class VulkanRenderer : IDisposable
{
    /// <summary>
    /// Kolik snímků smí být rozpracovaných zároveň.
    ///
    /// Dva jsou kompromis: s jedním by hlavní vlákno po každém odeslání čekalo na GPU
    /// a rozhodovalo by pomalejší z obou. S třemi a víc roste odezva vstupu, protože
    /// obraz, který se právě kreslí, je o tolik snímků starší než stisk klávesy.
    /// </summary>
    public const int FramesInFlight = 2;

    private readonly VulkanContext _context;
    private readonly VulkanSwapchain _swapchain;

    private readonly CommandPool[] _commandPools = new CommandPool[FramesInFlight];
    private readonly CommandBuffer[] _commandBuffers = new CommandBuffer[FramesInFlight];
    private readonly Semaphore[] _imageAvailable = new Semaphore[FramesInFlight];
    private readonly Fence[] _inFlight = new Fence[FramesInFlight];

    // Semafor "hotovo" patri OBRAZU, ne snimku. Kdyby byl na snimek, mohl by se cekat
    // na semafor, ktery jeste signalizuje jine, drive odeslane zobrazeni tehoz obrazu.
    private Semaphore[] _renderFinished = [];

    private VulkanBuffer? _captureBuffer;
    private CaptureRequest? _capture;
    private byte[]? _captureResult;

    private uint _imageIndex;
    private bool _frameActive;

    // Jestli už se v tomhle snímku odbočilo pro kopii scény. Rozdělit snímek jde jen
    // jednou — podruhé už by se kopírovalo přes vodu, kterou má odrážet.
    private bool _sceneCaptured;
    private bool _sceneOpen;

    public VulkanRenderer(VulkanContext context, VulkanSwapchain swapchain)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _swapchain = swapchain ?? throw new ArgumentNullException(nameof(swapchain));

        CreateFrameResources();
        CreatePerImageSemaphores();
    }

    /// <summary>Index rozpracovaného snímku, 0 až <see cref="FramesInFlight"/>−1.</summary>
    public int FrameSlot { get; private set; }

    /// <summary>Kolik snímků už proběhlo. Slouží k odloženému uvolňování bufferů.</summary>
    public long FrameCount { get; private set; }

    /// <summary>Příkazový buffer právě nahrávaného snímku.</summary>
    public CommandBuffer CommandBuffer => _commandBuffers[FrameSlot];

    /// <summary>Barva, kterou se čistí obrazovka.</summary>
    public Vector3 ClearColor { get; set; } = new(0f, 0f, 0f);

    /// <summary>
    /// Měření času grafiky, pokud je zapnuté. Renderer mu na začátku snímku vynuluje sadu
    /// značek — to se totiž smí jen mimo běžící rendering, kam se kreslení už nedostane.
    /// </summary>
    public VulkanGpuProfiler? GpuProfiler { get; set; }

    /// <summary>Nastane, když je potřeba předělat swapchain (změna velikosti okna).</summary>
    public bool NeedsResize { get; private set; }

    private sealed record CaptureRequest(int X, int Y, int Width, int Height);

    /// <summary>
    /// Zahájí snímek. Vrátí <c>false</c>, když je swapchain zastaralý a je potřeba ho
    /// předělat — volající pak snímek přeskočí.
    /// </summary>
    /// <summary>
    /// Jak dlouho se v posledním snímku čekalo na <c>vkWaitForFences</c>, tedy na to, až
    /// grafika dodělá dřívější snímek. Vysoké číslo znamená, že je grafika vytížená.
    /// </summary>
    public double LastFenceWaitMs { get; private set; }

    /// <summary>
    /// Jak dlouho se čekalo na <c>vkAcquireNextImageKHR</c>, tedy na volný obraz swapchainu.
    /// Vysoké číslo znamená, že brzdí <b>prezentace snímku</b> (skladba oken, obnovovací
    /// frekvence), ne grafika sama.
    /// </summary>
    public double LastAcquireMs { get; private set; }

    private static double ToMilliseconds(long ticks) =>
        ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

    public bool BeginFrame()
    {
        Vk vk = _context.Vk;
        Fence fence = _inFlight[FrameSlot];

        // Ceka se na snimek, ktery tenhle slot pouzival naposled. Az potom se smi
        // prepsat jeho prikazovy buffer a buffery, ktere cetl.
        //
        // Obe cekani se meri ZVLAST. Bez toho nejde odlisit "grafika ma co delat"
        // (vkWaitForFences) od "prezentace snimku brzdi" (vkAcquireNextImageKHR) —
        // a to jsou dve uplne jine priciny se dvema uplne jinymi opravami.
        long fenceStart = System.Diagnostics.Stopwatch.GetTimestamp();
        VulkanContext.Check(vk.WaitForFences(_context.Device, 1, &fence, true, ulong.MaxValue), "vkWaitForFences");

        long acquireStart = System.Diagnostics.Stopwatch.GetTimestamp();
        LastFenceWaitMs = ToMilliseconds(acquireStart - fenceStart);

        Result acquired = _context.KhrSwapchain.AcquireNextImage(
            _context.Device,
            _swapchain.Handle,
            ulong.MaxValue,
            _imageAvailable[FrameSlot],
            default,
            ref _imageIndex);

        LastAcquireMs = ToMilliseconds(System.Diagnostics.Stopwatch.GetTimestamp() - acquireStart);

        if (acquired is Result.ErrorOutOfDateKhr)
        {
            NeedsResize = true;
            return false;
        }

        if (acquired is not (Result.Success or Result.SuboptimalKhr))
        {
            throw new InvalidOperationException($"vkAcquireNextImageKHR selhalo: {acquired}.");
        }

        VulkanContext.Check(vk.ResetFences(_context.Device, 1, &fence), "vkResetFences");
        VulkanContext.Check(
            vk.ResetCommandPool(_context.Device, _commandPools[FrameSlot], 0), "vkResetCommandPool");

        CommandBuffer commandBuffer = _commandBuffers[FrameSlot];

        var beginInfo = new CommandBufferBeginInfo
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
        };

        VulkanContext.Check(vk.BeginCommandBuffer(commandBuffer, &beginInfo), "vkBeginCommandBuffer");

        // Nulování sady časových značek. Musí být TADY, mimo rendering — uvnitř
        // vkCmdBeginRendering to specifikace zakazuje.
        GpuProfiler?.Reset(commandBuffer, FrameSlot);

        // Obraz ze swapchainu prichazi v nedefinovanem layoutu a musi se prevest na
        // barevnou prilohu. V OpenGL nic takoveho nebylo - ovladac to resil sam.
        TransitionImage(
            commandBuffer,
            _swapchain.ImageAt((int)_imageIndex),
            ImageLayout.Undefined,
            ImageLayout.ColorAttachmentOptimal,
            PipelineStageFlags2.TopOfPipeBit,
            0,
            PipelineStageFlags2.ColorAttachmentOutputBit,
            AccessFlags2.ColorAttachmentWriteBit);

        TransitionImage(
            commandBuffer,
            _swapchain.HdrColorImage,
            ImageLayout.Undefined,
            ImageLayout.ColorAttachmentOptimal,
            PipelineStageFlags2.TopOfPipeBit,
            0,
            PipelineStageFlags2.ColorAttachmentOutputBit,
            AccessFlags2.ColorAttachmentWriteBit);

        // Hloubkový obraz potřebuje totéž. Vzniká v Undefined a nic ho odtamtud nedostane —
        // příloha níž ho přitom deklaruje jako DepthAttachmentOptimal, takže bez převodu
        // je to porušení specifikace. Ovladači to obvykle projde (LoadOp je Clear, takže
        // se stará data stejně zahazují), ale validační vrstva to hlásí jako chybu.
        //
        // Převádí se z Undefined každý snímek: tím se řekne "na obsahu nezáleží", což
        // přesně odpovídá LoadOp.Clear, a zároveň to samo přežije předělání swapchainu.
        TransitionImage(
            commandBuffer,
            _swapchain.DepthImage,
            ImageLayout.Undefined,
            ImageLayout.DepthAttachmentOptimal,
            PipelineStageFlags2.TopOfPipeBit,
            0,
            PipelineStageFlags2.EarlyFragmentTestsBit | PipelineStageFlags2.LateFragmentTestsBit,
            AccessFlags2.DepthStencilAttachmentWriteBit,
            _swapchain.DepthAspect);

        // Vícevzorkové cíle. Obojí z Undefined — na jejich obsahu z minulého snímku
        // nezáleží, barva se maže a hloubka se přepíše slitím.
        if (_swapchain.Samples != SampleCountFlags.Count1Bit)
        {
            TransitionImage(
                commandBuffer,
                _swapchain.MsaaColorImage,
                ImageLayout.Undefined,
                ImageLayout.ColorAttachmentOptimal,
                PipelineStageFlags2.TopOfPipeBit,
                0,
                PipelineStageFlags2.ColorAttachmentOutputBit,
                AccessFlags2.ColorAttachmentWriteBit);

            TransitionImage(
                commandBuffer,
                _swapchain.DepthSampleImage,
                ImageLayout.Undefined,
                ImageLayout.DepthAttachmentOptimal,
                PipelineStageFlags2.TopOfPipeBit,
                0,
                PipelineStageFlags2.LateFragmentTestsBit,
                AccessFlags2.DepthStencilAttachmentWriteBit,
                ImageAspectFlags.DepthBit);
        }

        _frameActive = true;
        _sceneCaptured = false;
        _sceneOpen = false;

        return true;
    }

    public void BeginScene()
    {
        if (!_frameActive || _sceneOpen)
        {
            return;
        }

        _sceneOpen = true;
        BeginRendering(_commandBuffers[FrameSlot]);
    }

    /// <param name="resumed">
    /// Pokračování rozděleného snímku. Přílohy se pak <b>načtou</b> místo vymazání a
    /// hloubka se připojí jen ke čtení, protože ve stejné chvíli visí i jako textura.
    /// </param>
    private void BeginRendering(CommandBuffer commandBuffer, bool resumed = false)
    {
        var clearColor = new ClearValue
        {
            Color = new ClearColorValue(ClearColor.X, ClearColor.Y, ClearColor.Z, 1f),
        };

        // VYHLAZENÍ HRAN. Kreslí se do vícevzorkové přílohy a Vulkan ji na konci úseku sám
        // slije do obrazu swapchainu — právě to rozostří schodovité hrany. Při jednom
        // vzorku se kreslí rovnou do swapchainu a slévání se nekoná.
        bool msaa = _swapchain.Samples != SampleCountFlags.Count1Bit;

        var colorAttachment = new RenderingAttachmentInfo
        {
            SType = StructureType.RenderingAttachmentInfo,
            ImageView = msaa ? _swapchain.MsaaColorView : _swapchain.HdrColorView,
            ImageLayout = ImageLayout.ColorAttachmentOptimal,

            // Průměr vzorků, protože jde o barvu.
            ResolveMode = msaa ? ResolveModeFlags.AverageBit : ResolveModeFlags.None,
            ResolveImageView = msaa ? _swapchain.HdrColorView : default,
            ResolveImageLayout = msaa ? ImageLayout.ColorAttachmentOptimal : ImageLayout.Undefined,

            LoadOp = resumed ? AttachmentLoadOp.Load : AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.Store,
            ClearValue = clearColor,
        };

        var depthAttachment = new RenderingAttachmentInfo
        {
            SType = StructureType.RenderingAttachmentInfo,
            ImageView = _swapchain.DepthView,

            // Po rozdělení snímku je hloubka zároveň přílohou a texturou. Jediný layout,
            // ve kterém smí být obojím, je DepthReadOnlyOptimal — hloubkový test z něj
            // pořád čte, jen se do něj nesmí zapisovat. Voda zápis vypnutý má.
            ImageLayout = resumed ? ImageLayout.DepthReadOnlyOptimal : ImageLayout.DepthAttachmentOptimal,

            // PRŮMĚR SE U HLOUBKY POUŽÍT NESMÍ — zprůměrovat vzdálenosti dvou různých
            // povrchů dá hodnotu, na které neleží ani jeden. Bere se proto vzorek nula.
            // Voda z té kopie hledá průsečík odrazu a na to je to dost přesné.
            //
            // Ve druhém úseku se neslévá: kopie je tam už hotová a voda z ní ČTE. Týž obraz
            // nemůže být zároveň cílem slití a čtenou texturou.
            ResolveMode = msaa && !resumed ? ResolveModeFlags.SampleZeroBit : ResolveModeFlags.None,
            ResolveImageView = msaa && !resumed ? _swapchain.DepthSampleView : default,
            ResolveImageLayout = msaa && !resumed
                ? ImageLayout.DepthAttachmentOptimal
                : ImageLayout.Undefined,

            LoadOp = resumed ? AttachmentLoadOp.Load : AttachmentLoadOp.Clear,

            // Store, ne DontCare: po rozdělení se z hloubky ještě čte ve vodním průchodu.
            // S DontCare by ovladač směl obsah zahodit hned na konci prvního úseku.
            StoreOp = AttachmentStoreOp.Store,

            ClearValue = new ClearValue { DepthStencil = new ClearDepthStencilValue(1f, 0) },
        };

        var renderingInfo = new RenderingInfo
        {
            SType = StructureType.RenderingInfo,
            RenderArea = new Rect2D(new Offset2D(0, 0), _swapchain.Extent),
            LayerCount = 1,
            ColorAttachmentCount = 1,
            PColorAttachments = &colorAttachment,
            PDepthAttachment = &depthAttachment,
        };

        _context.Vk.CmdBeginRendering(commandBuffer, &renderingInfo);

        var viewport = new Viewport
        {
            X = 0f,
            Y = 0f,
            Width = _swapchain.Extent.Width,
            Height = _swapchain.Extent.Height,
            MinDepth = 0f,
            MaxDepth = 1f,
        };

        var scissor = new Rect2D(new Offset2D(0, 0), _swapchain.Extent);

        _context.Vk.CmdSetViewport(commandBuffer, 0, 1, &viewport);
        _context.Vk.CmdSetScissor(commandBuffer, 0, 1, &scissor);
    }

    /// <summary>
    /// Rozdělí snímek a zpřístupní vodnímu shaderu hotovou scénu i hloubku.
    /// </summary>
    /// <remarks>
    /// <para><b>Proč to musí přerušit kreslení.</b> Screen-space odraz i lom se ptají na to,
    /// co už je na obrazovce. Dokud běží <c>vkCmdBeginRendering</c>, jsou obraz i hloubka
    /// přílohami a číst z nich jako z textury se nesmí — grafika totiž nemá jak zaručit,
    /// že fragment, na který se ptáme, už doběhl. Úsek se proto ukončí, obraz se zkopíruje
    /// stranou a kreslení pokračuje s <c>LoadOp.Load</c>.</para>
    ///
    /// <para><b>Volá se až po vší neprůhledné geometrii a před vodou.</b> Dřív by v kopii
    /// chyběl terén, který má voda odrážet; později už není co dělit.</para>
    ///
    /// <para>Za snímek se smí zavolat jen jednou — druhé volání se tiše ignoruje, aby
    /// nezáleželo na tom, kolik průchodů vody nakonec bude.</para>
    /// </remarks>
    public void CaptureSceneForWater() => CaptureScene(once: true);

    /// <summary>
    /// Zkopíruje hotový obraz do samplovatelné textury a pokračuje v kreslení.
    /// </summary>
    /// <param name="once">
    /// Když true, za snímek se provede jen poprvé — tak to potřebuje voda, aby nezáleželo
    /// na počtu jejích průchodů. Post-process naopak chce čerstvou kopii i podruhé, protože
    /// mezitím přibyla právě ta voda.
    /// </param>
    public void CaptureScene(bool once)
    {
        bool firstCapture = !_sceneCaptured;
        if (!_frameActive || (once && !firstCapture))
        {
            return;
        }

        _sceneCaptured = true;

        CommandBuffer commandBuffer = _commandBuffers[FrameSlot];
        Image image = _swapchain.HdrColorImage;

        _context.Vk.CmdEndRendering(commandBuffer);

        TransitionImage(
            commandBuffer,
            image,
            ImageLayout.ColorAttachmentOptimal,
            ImageLayout.TransferSrcOptimal,
            PipelineStageFlags2.ColorAttachmentOutputBit,
            AccessFlags2.ColorAttachmentWriteBit,
            PipelineStageFlags2.AllTransferBit,
            AccessFlags2.TransferReadBit);

        // Z Undefined: na předchozím obsahu kopie nezáleží, celá se hned přepíše. Říct to
        // takhle je levnější než zachovávat data, která nikdo nechce.
        TransitionImage(
            commandBuffer,
            _swapchain.SceneImage,
            ImageLayout.Undefined,
            ImageLayout.TransferDstOptimal,
            PipelineStageFlags2.TopOfPipeBit,
            0,
            PipelineStageFlags2.AllTransferBit,
            AccessFlags2.TransferWriteBit);

        var layers = new ImageSubresourceLayers
        {
            AspectMask = ImageAspectFlags.ColorBit,
            MipLevel = 0,
            BaseArrayLayer = 0,
            LayerCount = 1,
        };

        var copy = new ImageCopy
        {
            SrcSubresource = layers,
            DstSubresource = layers,
            SrcOffset = new Offset3D(0, 0, 0),
            DstOffset = new Offset3D(0, 0, 0),
            Extent = new Extent3D(_swapchain.Extent.Width, _swapchain.Extent.Height, 1),
        };

        _context.Vk.CmdCopyImage(
            commandBuffer,
            image,
            ImageLayout.TransferSrcOptimal,
            _swapchain.SceneImage,
            ImageLayout.TransferDstOptimal,
            1,
            &copy);

        TransitionImage(
            commandBuffer,
            _swapchain.SceneImage,
            ImageLayout.TransferDstOptimal,
            ImageLayout.ShaderReadOnlyOptimal,
            PipelineStageFlags2.AllTransferBit,
            AccessFlags2.TransferWriteBit,
            PipelineStageFlags2.FragmentShaderBit,
            AccessFlags2.ShaderSampledReadBit);

        TransitionImage(
            commandBuffer,
            image,
            ImageLayout.TransferSrcOptimal,
            ImageLayout.ColorAttachmentOptimal,
            PipelineStageFlags2.AllTransferBit,
            AccessFlags2.TransferReadBit,
            PipelineStageFlags2.ColorAttachmentOutputBit,
            AccessFlags2.ColorAttachmentWriteBit);

        // Hloubka zůstává přílohou a zároveň se z ní začne číst. Čeká se na pozdní
        // hloubkový test, protože to je poslední místo, kde do ní neprůhledný průchod psal.
        if (firstCapture)
        {
            TransitionImage(
                commandBuffer,
                _swapchain.DepthImage,
                ImageLayout.DepthAttachmentOptimal,
                ImageLayout.DepthReadOnlyOptimal,
                PipelineStageFlags2.LateFragmentTestsBit,
                AccessFlags2.DepthStencilAttachmentWriteBit,
                PipelineStageFlags2.FragmentShaderBit | PipelineStageFlags2.EarlyFragmentTestsBit,
                AccessFlags2.ShaderSampledReadBit | AccessFlags2.DepthStencilAttachmentReadBit,
                _swapchain.DepthAspect);
        }

        // Při vyhlazení hran čte voda ze slité kopie, ne z přílohy — ta je vícevzorková
        // a obyčejným samplerem se přečíst nedá. Slití proběhlo právě teď, na konci
        // předchozího úseku, takže se čeká na ně.
        if (firstCapture && _swapchain.Samples != SampleCountFlags.Count1Bit)
        {
            TransitionImage(
                commandBuffer,
                _swapchain.DepthSampleImage,
                ImageLayout.DepthAttachmentOptimal,
                ImageLayout.DepthReadOnlyOptimal,
                PipelineStageFlags2.LateFragmentTestsBit,
                AccessFlags2.DepthStencilAttachmentWriteBit,
                PipelineStageFlags2.FragmentShaderBit,
                AccessFlags2.ShaderSampledReadBit,
                ImageAspectFlags.DepthBit);
        }

        BeginRendering(commandBuffer, resumed: true);
    }

    /// <summary>
    /// Finishes the floating-point world pass and opens the display/UI pass on the swapchain.
    /// The HDR result remains shader-readable so the tonemapper can sample it immediately.
    /// </summary>
    public void BeginPresentation()
    {
        if (!_frameActive || !_sceneOpen)
        {
            return;
        }

        CommandBuffer commandBuffer = _commandBuffers[FrameSlot];
        _context.Vk.CmdEndRendering(commandBuffer);

        TransitionImage(
            commandBuffer,
            _swapchain.HdrColorImage,
            ImageLayout.ColorAttachmentOptimal,
            ImageLayout.ShaderReadOnlyOptimal,
            PipelineStageFlags2.ColorAttachmentOutputBit,
            AccessFlags2.ColorAttachmentWriteBit,
            PipelineStageFlags2.FragmentShaderBit,
            AccessFlags2.ShaderSampledReadBit);

        var colorAttachment = new RenderingAttachmentInfo
        {
            SType = StructureType.RenderingAttachmentInfo,
            ImageView = _swapchain.ViewAt((int)_imageIndex),
            ImageLayout = ImageLayout.ColorAttachmentOptimal,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.Store,
            ClearValue = new ClearValue
            {
                Color = new ClearColorValue(ClearColor.X, ClearColor.Y, ClearColor.Z, 1f),
            },
        };

        var renderingInfo = new RenderingInfo
        {
            SType = StructureType.RenderingInfo,
            RenderArea = new Rect2D(new Offset2D(0, 0), _swapchain.Extent),
            LayerCount = 1,
            ColorAttachmentCount = 1,
            PColorAttachments = &colorAttachment,
        };

        _context.Vk.CmdBeginRendering(commandBuffer, &renderingInfo);

        var viewport = new Viewport(
            0f, 0f, _swapchain.Extent.Width, _swapchain.Extent.Height, 0f, 1f);
        var scissor = new Rect2D(new Offset2D(0, 0), _swapchain.Extent);
        _context.Vk.CmdSetViewport(commandBuffer, 0, 1, &viewport);
        _context.Vk.CmdSetScissor(commandBuffer, 0, 1, &scissor);
    }

    /// <summary>
    /// Požádá o přečtení části obrazovky v tomhle snímku. Nahrazuje <c>glReadPixels</c>
    /// a používá to jenom selftest.
    /// </summary>
    public void RequestCapture(int x, int y, int width, int height) =>
        _capture = new CaptureRequest(x, y, width, height);

    /// <summary>
    /// Vyzvedne přečtený obraz. Data jsou <b>RGBA, řádky shora dolů</b>.
    ///
    /// <para>
    /// Obojí se liší od OpenGL: swapchain má formát BGRA (převádí se tady) a Vulkan má
    /// počátek obrazu vlevo <b>nahoře</b>, kdežto <c>glReadPixels</c> četl zdola nahoru.
    /// Volající proto nesmí obracet řádky jako dřív.
    /// </para>
    /// </summary>
    public byte[]? TakeCapture()
    {
        byte[]? result = _captureResult;
        _captureResult = null;
        return result;
    }

    /// <summary>Ukončí snímek, odešle ho a zobrazí.</summary>
    public void EndFrame()
    {
        if (!_frameActive)
        {
            return;
        }

        _frameActive = false;

        Vk vk = _context.Vk;
        CommandBuffer commandBuffer = _commandBuffers[FrameSlot];
        Image image = _swapchain.ImageAt((int)_imageIndex);

        vk.CmdEndRendering(commandBuffer);

        bool capturing = _capture is not null;
        if (capturing)
        {
            RecordCapture(commandBuffer, image, _capture!);
        }
        else
        {
            TransitionImage(
                commandBuffer,
                image,
                ImageLayout.ColorAttachmentOptimal,
                ImageLayout.PresentSrcKhr,
                PipelineStageFlags2.ColorAttachmentOutputBit,
                AccessFlags2.ColorAttachmentWriteBit,
                PipelineStageFlags2.BottomOfPipeBit,
                0);
        }

        VulkanContext.Check(vk.EndCommandBuffer(commandBuffer), "vkEndCommandBuffer");

        Semaphore waitSemaphore = _imageAvailable[FrameSlot];
        Semaphore signalSemaphore = _renderFinished[(int)_imageIndex];
        var waitStage = PipelineStageFlags.ColorAttachmentOutputBit;

        var submit = new SubmitInfo
        {
            SType = StructureType.SubmitInfo,
            WaitSemaphoreCount = 1,
            PWaitSemaphores = &waitSemaphore,
            PWaitDstStageMask = &waitStage,
            CommandBufferCount = 1,
            PCommandBuffers = &commandBuffer,
            SignalSemaphoreCount = 1,
            PSignalSemaphores = &signalSemaphore,
        };

        VulkanContext.Check(vk.QueueSubmit(_context.Queue, 1, &submit, _inFlight[FrameSlot]), "vkQueueSubmit");

        if (capturing)
        {
            FinishCapture(_capture!);
            _capture = null;
        }

        SwapchainKHR swapchain = _swapchain.Handle;
        uint imageIndex = _imageIndex;

        var present = new PresentInfoKHR
        {
            SType = StructureType.PresentInfoKhr,
            WaitSemaphoreCount = 1,
            PWaitSemaphores = &signalSemaphore,
            SwapchainCount = 1,
            PSwapchains = &swapchain,
            PImageIndices = &imageIndex,
        };

        Result presented = _context.KhrSwapchain.QueuePresent(_context.Queue, &present);

        if (presented is Result.ErrorOutOfDateKhr or Result.SuboptimalKhr)
        {
            NeedsResize = true;
        }
        else if (presented != Result.Success)
        {
            throw new InvalidOperationException($"vkQueuePresentKHR selhalo: {presented}.");
        }

        FrameSlot = (FrameSlot + 1) % FramesInFlight;
        FrameCount++;
    }

    /// <summary>
    /// Předělá swapchain a s ním i semafory vázané na obrazy. Volá se po změně velikosti okna.
    /// </summary>
    public void HandleResize(int width, int height, bool vsync)
    {
        _context.Vk.DeviceWaitIdle(_context.Device);

        DestroyPerImageSemaphores();
        _swapchain.Recreate(width, height, vsync);
        CreatePerImageSemaphores();

        NeedsResize = false;
    }

    private void RecordCapture(CommandBuffer commandBuffer, Image image, CaptureRequest request)
    {
        EnsureCaptureBuffer(request);

        TransitionImage(
            commandBuffer,
            image,
            ImageLayout.ColorAttachmentOptimal,
            ImageLayout.TransferSrcOptimal,
            PipelineStageFlags2.ColorAttachmentOutputBit,
            AccessFlags2.ColorAttachmentWriteBit,
            PipelineStageFlags2.AllTransferBit,
            AccessFlags2.TransferReadBit);

        var region = new BufferImageCopy
        {
            BufferOffset = 0,
            BufferRowLength = 0,
            BufferImageHeight = 0,
            ImageSubresource = new ImageSubresourceLayers
            {
                AspectMask = ImageAspectFlags.ColorBit,
                MipLevel = 0,
                BaseArrayLayer = 0,
                LayerCount = 1,
            },
            ImageOffset = new Offset3D(request.X, request.Y, 0),
            ImageExtent = new Extent3D((uint)request.Width, (uint)request.Height, 1),
        };

        _context.Vk.CmdCopyImageToBuffer(
            commandBuffer, image, ImageLayout.TransferSrcOptimal, _captureBuffer!.Handle, 1, &region);

        TransitionImage(
            commandBuffer,
            image,
            ImageLayout.TransferSrcOptimal,
            ImageLayout.PresentSrcKhr,
            PipelineStageFlags2.AllTransferBit,
            AccessFlags2.TransferReadBit,
            PipelineStageFlags2.BottomOfPipeBit,
            0);
    }

    private void EnsureCaptureBuffer(CaptureRequest request)
    {
        ulong needed = (ulong)request.Width * (ulong)request.Height * 4UL;

        if (_captureBuffer is not null && _captureBuffer.Capacity >= needed)
        {
            return;
        }

        _captureBuffer?.Dispose();
        // forReading: z tohohle bufferu se čte na CPU, takže musí být v cachované systémové
        // paměti. V paměti grafiky (write-combined) trvalo přečtení snímku dvě vteřiny.
        _captureBuffer = VulkanBuffer.CreateHostVisible(
            _context, needed, BufferUsageFlags.TransferDstBit, forReading: true);
    }

    /// <summary>
    /// Počká na dokončení snímku a přečte zkopírovaná data. Čekání je tu v pořádku:
    /// děje se to jen v posledních snímcích selftestu, ne v běžné hře.
    /// </summary>
    private void FinishCapture(CaptureRequest request)
    {
        Fence fence = _inFlight[FrameSlot];
        VulkanContext.Check(
            _context.Vk.WaitForFences(_context.Device, 1, &fence, true, ulong.MaxValue), "vkWaitForFences (capture)");

        int pixels = request.Width * request.Height;
        ReadOnlySpan<byte> source = _captureBuffer!.Read(pixels * 4);

        byte[] rgba = new byte[pixels * 4];
        for (int i = 0; i < pixels; i++)
        {
            int offset = i * 4;

            // Swapchain je B8G8R8A8 (vybrano zamerne kvuli shode barev s OpenGL verzi),
            // ale volajici ocekava RGBA - prohodi se modra s cervenou.
            rgba[offset + 0] = source[offset + 2];
            rgba[offset + 1] = source[offset + 1];
            rgba[offset + 2] = source[offset + 0];
            rgba[offset + 3] = source[offset + 3];
        }

        _captureResult = rgba;
    }

    /// <summary>
    /// Změna layoutu obrazu. Ve Vulkanu se o rozvržení dat v obrazu stará program, ne
    /// ovladač — obraz se musí explicitně převést do stavu, který další krok očekává,
    /// a zároveň se tím řekne, na co má GPU počkat.
    /// </summary>
    private void TransitionImage(
        CommandBuffer commandBuffer,
        Image image,
        ImageLayout from,
        ImageLayout to,
        PipelineStageFlags2 sourceStage,
        AccessFlags2 sourceAccess,
        PipelineStageFlags2 destinationStage,
        AccessFlags2 destinationAccess,
        ImageAspectFlags aspect = ImageAspectFlags.ColorBit)
    {
        var barrier = new ImageMemoryBarrier2
        {
            SType = StructureType.ImageMemoryBarrier2,
            SrcStageMask = sourceStage,
            SrcAccessMask = sourceAccess,
            DstStageMask = destinationStage,
            DstAccessMask = destinationAccess,
            OldLayout = from,
            NewLayout = to,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = image,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = aspect,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1,
            },
        };

        var dependency = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            ImageMemoryBarrierCount = 1,
            PImageMemoryBarriers = &barrier,
        };

        _context.Vk.CmdPipelineBarrier2(commandBuffer, &dependency);
    }

    private void CreateFrameResources()
    {
        Vk vk = _context.Vk;

        for (int i = 0; i < FramesInFlight; i++)
        {
            var poolInfo = new CommandPoolCreateInfo
            {
                SType = StructureType.CommandPoolCreateInfo,
                QueueFamilyIndex = _context.QueueFamily,
                Flags = CommandPoolCreateFlags.TransientBit,
            };

            VulkanContext.Check(
                vk.CreateCommandPool(_context.Device, &poolInfo, null, out _commandPools[i]), "vkCreateCommandPool");

            var allocateInfo = new CommandBufferAllocateInfo
            {
                SType = StructureType.CommandBufferAllocateInfo,
                CommandPool = _commandPools[i],
                Level = CommandBufferLevel.Primary,
                CommandBufferCount = 1,
            };

            VulkanContext.Check(
                vk.AllocateCommandBuffers(_context.Device, &allocateInfo, out _commandBuffers[i]),
                "vkAllocateCommandBuffers");

            var semaphoreInfo = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo };
            VulkanContext.Check(
                vk.CreateSemaphore(_context.Device, &semaphoreInfo, null, out _imageAvailable[i]), "vkCreateSemaphore");

            // Plot se zaklada uz signalizovany, jinak by prvni WaitForFences cekal navzdy.
            var fenceInfo = new FenceCreateInfo
            {
                SType = StructureType.FenceCreateInfo,
                Flags = FenceCreateFlags.SignaledBit,
            };

            VulkanContext.Check(vk.CreateFence(_context.Device, &fenceInfo, null, out _inFlight[i]), "vkCreateFence");
        }
    }

    private void CreatePerImageSemaphores()
    {
        _renderFinished = new Semaphore[_swapchain.ImageCount];

        for (int i = 0; i < _renderFinished.Length; i++)
        {
            var info = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo };
            VulkanContext.Check(
                _context.Vk.CreateSemaphore(_context.Device, &info, null, out _renderFinished[i]), "vkCreateSemaphore");
        }
    }

    private void DestroyPerImageSemaphores()
    {
        foreach (Semaphore semaphore in _renderFinished)
        {
            if (semaphore.Handle != 0)
            {
                _context.Vk.DestroySemaphore(_context.Device, semaphore, null);
            }
        }

        _renderFinished = [];
    }

    public void Dispose()
    {
        _context.Vk.DeviceWaitIdle(_context.Device);

        _captureBuffer?.Dispose();
        _captureBuffer = null;

        DestroyPerImageSemaphores();

        for (int i = 0; i < FramesInFlight; i++)
        {
            if (_imageAvailable[i].Handle != 0)
            {
                _context.Vk.DestroySemaphore(_context.Device, _imageAvailable[i], null);
            }

            if (_inFlight[i].Handle != 0)
            {
                _context.Vk.DestroyFence(_context.Device, _inFlight[i], null);
            }

            if (_commandPools[i].Handle != 0)
            {
                _context.Vk.DestroyCommandPool(_context.Device, _commandPools[i], null);
            }
        }
    }
}
