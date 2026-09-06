using Silk.NET.Vulkan;
using Tesseris.Engine.Core;

namespace Tesseris.Engine.Rendering.Vulkan;

/// <summary>
/// Swapchain, jeho obrazy a hloubkový buffer — tedy všechno, co se musí předělat, když se
/// změní velikost okna.
/// </summary>
public sealed unsafe class VulkanSwapchain : IDisposable
{
    /// <summary>
    /// Barevný formát. Vybírá se <b>Unorm</b>, ne Srgb, a je to záměr: OpenGL verze kreslila
    /// do obyčejného (ne-sRGB) framebufferu, takže hodnota 0,62 skončila v paměti jako 158.
    /// Se Srgb formátem by ovladač barvu ještě převedl a celá scéna by zesvětlala — obrázek
    /// by se lišil od původní hry, aniž by se cokoli změnilo v shaderu.
    /// </summary>
    private static readonly Format PreferredFormat = Format.B8G8R8A8Unorm;

    /// <summary>Linear floating-point format used by the complete world-rendering pipeline.</summary>
    public const Format SceneColorFormat = Format.R16G16B16A16Sfloat;

    private readonly VulkanContext _context;

    private SwapchainKHR _swapchain;
    private Image[] _images = [];
    private ImageView[] _views = [];

    private Image _depthImage;
    private DeviceMemory _depthMemory;
    private ImageView _depthView;

    // Kopie hotové scény pro vodu. Vlastní obraz, ne obraz swapchainu: číst z téhož
    // obrazu, do kterého se zároveň kreslí, Vulkan zakazuje a ovladač na to nemá jak
    // upozornit — vyšel by z toho nedefinovaný obsah závislý na pořadí fragmentů.
    private Image _sceneImage;
    private DeviceMemory _sceneMemory;
    private ImageView _sceneView;
    private Sampler _sceneSampler;
    private Sampler _depthSampler;

    // Single-sample HDR result. With MSAA enabled the multisampled attachment resolves here;
    // without MSAA the world renders here directly. The presentation pass samples this image.
    private Image _hdrColorImage;
    private DeviceMemory _hdrColorMemory;
    private ImageView _hdrColorView;

    // Druhý pohled na TÝŽ hloubkový obraz, jen s aspektem Depth.
    //
    // Nejde použít _depthView: ten je přílohou, a když má formát stencil, nese aspekty
    // oba. Vulkan ale zakazuje, aby obraz vázaný jako sampler měl aspektů víc než jeden.
    // Attachment tedy potřebuje pohled s obojím a shader pohled jen s hloubkou.
    private ImageView _depthSampleView;

    // Vyhlazení hran (MSAA). Kreslí se do vícevzorkových příloh a teprve výsledek se
    // slije do obrazu swapchainu — to je ten krok, který schodovité hrany rozostří.
    //
    // Při jednom vzorku zůstanou prázdné a kreslí se rovnou do swapchainu jako dřív.
    private Image _msaaColorImage;
    private DeviceMemory _msaaColorMemory;
    private ImageView _msaaColorView;

    // Jednovzorková kopie hloubky pro vodu.
    //
    // <b>Bez ní by screen-space odraz přestal fungovat.</b> Vícevzorkový obraz se nedá
    // číst obyčejným samplerem, a voda hloubku potřebuje. Slévá se do ní na konci
    // neprůhledného úseku módem „vezmi vzorek nula" — pro hledání průsečíku je to dost
    // přesné a je to nejlevnější možný způsob.
    private Image _depthResolveImage;
    private DeviceMemory _depthResolveMemory;
    private ImageView _depthResolveView;

    public VulkanSwapchain(VulkanContext context, int width, int height, bool vsync)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        Create(width, height, vsync);
    }

    public Format ColorFormat { get; private set; }

    public Extent2D Extent { get; private set; }

    public int ImageCount => _images.Length;

    public bool Vsync { get; private set; }

    public Image ImageAt(int index) => _images[index];

    public ImageView ViewAt(int index) => _views[index];

    public ImageView DepthView => _depthView;

    public Image DepthImage => _depthImage;

    /// <summary>Kopie scény, ze které čte vodní shader při screen-space odrazu a lomu.</summary>
    public Image SceneImage => _sceneImage;

    /// <summary>Pohled na <see cref="SceneImage"/>.</summary>
    public ImageView SceneView => _sceneView;

    public Image HdrColorImage => _hdrColorImage;

    public ImageView HdrColorView => _hdrColorView;

    /// <summary>
    /// Pohled na hloubku pro čtení v shaderu. Má vždy jen aspekt hloubky a vždy jeden
    /// vzorek — při zapnutém vyhlazení je to slitá kopie, jinak přímo hloubková příloha.
    /// </summary>
    public ImageView DepthSampleView => Samples == SampleCountFlags.Count1Bit
        ? _depthSampleView
        : _depthResolveView;

    /// <summary>Obraz, ze kterého voda čte hloubku. Řídí se toutéž úvahou jako pohled.</summary>
    public Image DepthSampleImage => Samples == SampleCountFlags.Count1Bit
        ? _depthImage
        : _depthResolveImage;

    /// <summary>Kolik vzorků na pixel se právě kreslí. Jeden znamená vyhlazení vypnuté.</summary>
    public SampleCountFlags Samples { get; private set; } = SampleCountFlags.Count1Bit;

    /// <summary>
    /// Vícevzorková barevná příloha, nebo prázdná při vypnutém vyhlazení. Renderer podle
    /// toho pozná, jestli má kreslit do ní a slévat, nebo rovnou do swapchainu.
    /// </summary>
    public ImageView MsaaColorView => _msaaColorView;

    /// <summary>Vícevzorková barevná příloha. Renderer ji převádí do layoutu na začátku snímku.</summary>
    public Image MsaaColorImage => _msaaColorImage;

    /// <summary>
    /// Sampler kopie scény. <b>Lineární</b>: odražený paprsek trefí obrazovku mezi pixely
    /// a nejbližší soused by z odrazu udělal schody.
    /// </summary>
    public Sampler SceneSampler => _sceneSampler;

    /// <summary>
    /// Sampler hloubky. <b>Nearest, a to je nutnost.</b> Hloubka není barva — průměr dvou
    /// sousedních hodnot leží v prázdnu mezi dvěma povrchy a raymarch by na hranách
    /// geometrie hlásil zásah tam, kde nic není.
    /// </summary>
    public Sampler DepthSampler => _depthSampler;

    /// <summary>
    /// Aspekt hloubkového obrazu. Musí sedět na formát: D32_SFLOAT nemá stencil,
    /// D32_SFLOAT_S8_UINT ano. Potřebuje ho i renderer, když obraz převádí do layoutu.
    /// </summary>
    public ImageAspectFlags DepthAspect => _context.DepthFormat == Format.D32Sfloat
        ? ImageAspectFlags.DepthBit
        : ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit;

    public SwapchainKHR Handle => _swapchain;

    /// <summary>
    /// Předělá swapchain na novou velikost. Volá se při změně velikosti okna a taky když
    /// <c>vkAcquireNextImageKHR</c> ohlásí, že je swapchain zastaralý.
    /// </summary>
    public void Recreate(int width, int height, bool vsync)
    {
        _context.Vk.DeviceWaitIdle(_context.Device);
        DestroyResources();
        Create(width, height, vsync);
    }

    private void Create(int width, int height, bool vsync)
    {
        Vsync = vsync;

        _context.KhrSurface.GetPhysicalDeviceSurfaceCapabilities(
            _context.PhysicalDevice, _context.Surface, out SurfaceCapabilitiesKHR capabilities);

        SurfaceFormatKHR surfaceFormat = ChooseFormat();
        PresentModeKHR presentMode = ChoosePresentMode(vsync);
        Extent2D extent = ChooseExtent(capabilities, width, height);

        // O jeden obraz víc, než je minimum: s minimem by hlavní vlákno čekalo, až ovladač
        // uvolní ten jediný volný. Strop se musí respektovat, maxImageCount == 0 znamená
        // "bez omezení".
        uint imageCount = capabilities.MinImageCount + 1;
        if (capabilities.MaxImageCount > 0 && imageCount > capabilities.MaxImageCount)
        {
            imageCount = capabilities.MaxImageCount;
        }

        var info = new SwapchainCreateInfoKHR
        {
            SType = StructureType.SwapchainCreateInfoKhr,
            Surface = _context.Surface,
            MinImageCount = imageCount,
            ImageFormat = surfaceFormat.Format,
            ImageColorSpace = surfaceFormat.ColorSpace,
            ImageExtent = extent,
            ImageArrayLayers = 1,

            // TransferSrc je tu kvůli selftestu: ten čte hotový snímek zpátky do paměti,
            // což je vulkanská náhrada za glReadPixels. Že to swapchain umí, bylo ověřeno
            // měřením ještě před portem.
            ImageUsage = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferSrcBit,

            ImageSharingMode = SharingMode.Exclusive,
            PreTransform = capabilities.CurrentTransform,
            CompositeAlpha = CompositeAlphaFlagsKHR.OpaqueBitKhr,
            PresentMode = presentMode,
            Clipped = true,
            OldSwapchain = default,
        };

        VulkanContext.Check(
            _context.KhrSwapchain.CreateSwapchain(_context.Device, &info, null, out _swapchain),
            "vkCreateSwapchainKHR");

        ColorFormat = surfaceFormat.Format;
        Extent = extent;

        uint actualCount = 0;
        _context.KhrSwapchain.GetSwapchainImages(_context.Device, _swapchain, ref actualCount, null);

        _images = new Image[actualCount];
        fixed (Image* imagesPtr = _images)
        {
            _context.KhrSwapchain.GetSwapchainImages(_context.Device, _swapchain, ref actualCount, imagesPtr);
        }

        _views = new ImageView[actualCount];
        for (int i = 0; i < actualCount; i++)
        {
            _views[i] = CreateView(_images[i], ColorFormat, ImageAspectFlags.ColorBit);
        }

        Samples = _context.MaxSamples;

        CreateMsaaTargets(extent);
        CreateDepthBuffer(extent);
        CreateSceneTarget(extent);
        CreateSamplers();

        Log.Info(
            $"Swapchain {extent.Width}x{extent.Height}, {actualCount} obrazů, "
            + $"{ColorFormat}, {presentMode}{(vsync ? "" : " (vsync vypnutý)")}.");
    }

    /// <summary>Nabízí zařízení tenhle režim zobrazování?</summary>
    private bool HasPresentMode(PresentModeKHR wanted)
    {
        uint count = 0;
        _context.KhrSurface.GetPhysicalDeviceSurfacePresentModes(
            _context.PhysicalDevice, _context.Surface, ref count, null);

        var modes = new PresentModeKHR[count];
        fixed (PresentModeKHR* modesPtr = modes)
        {
            _context.KhrSurface.GetPhysicalDeviceSurfacePresentModes(
                _context.PhysicalDevice, _context.Surface, ref count, modesPtr);
        }

        return Array.IndexOf(modes, wanted) >= 0;
    }

    private SurfaceFormatKHR ChooseFormat()
    {
        uint count = 0;
        _context.KhrSurface.GetPhysicalDeviceSurfaceFormats(
            _context.PhysicalDevice, _context.Surface, ref count, null);

        var formats = new SurfaceFormatKHR[count];
        fixed (SurfaceFormatKHR* formatsPtr = formats)
        {
            _context.KhrSurface.GetPhysicalDeviceSurfaceFormats(
                _context.PhysicalDevice, _context.Surface, ref count, formatsPtr);
        }

        foreach (SurfaceFormatKHR format in formats)
        {
            if (format.Format == PreferredFormat && format.ColorSpace == ColorSpaceKHR.SpaceSrgbNonlinearKhr)
            {
                return format;
            }
        }

        Log.Warn($"{PreferredFormat} není k dispozici, beru {formats[0].Format} — barvy se můžou lišit.");
        return formats[0];
    }

    /// <summary>
    /// MAILBOX při zapnutém vsyncu (když ho zařízení umí), jinak FIFO. Při vypnutém
    /// vsyncu IMMEDIATE.
    /// </summary>
    /// <remarks>
    /// <para><b>Proč MAILBOX a ne FIFO.</b> FIFO čeká na obnovu obrazu: když snímek
    /// o kousek přeteče její interval, nestihne se a čeká se na <b>celou další</b>. Ze
    /// stošedesáti snímků je rázem osmdesát, i když scéna zvládala sto čtyřicet.</para>
    ///
    /// <para><b>Naměřeno přesně takhle:</b> log ukazoval sérii snímků po 12,9 až 13,1 ms,
    /// tedy dva intervaly 161Hz monitoru za sebou. Nebyl to propad výkonu, ale způsob,
    /// jakým FIFO čeká.</para>
    ///
    /// <para>MAILBOX místo čekání jen nahradí dosud nezobrazený snímek novějším. Obraz se
    /// pořád mění jen při obnově, takže se netrhá, ale nedokončený snímek nestojí celý
    /// interval. Cena je jeden obraz navíc v paměti a to, že se část práce zahodí.</para>
    ///
    /// <para><b>MoltenVK ho nenabízí</b> (ověřeno měřením: jen FIFO a IMMEDIATE), takže na
    /// Macu se stejně použije FIFO — proto se tu nabídka prochází a nespoléhá se na ni.</para>
    /// </remarks>
    private PresentModeKHR ChoosePresentMode(bool vsync)
    {
        if (vsync)
        {
            return HasPresentMode(PresentModeKHR.MailboxKhr)
                ? PresentModeKHR.MailboxKhr

                // FIFO musí podle specifikace umět každé zařízení.
                : PresentModeKHR.FifoKhr;
        }

        uint count = 0;
        _context.KhrSurface.GetPhysicalDeviceSurfacePresentModes(
            _context.PhysicalDevice, _context.Surface, ref count, null);

        var modes = new PresentModeKHR[count];
        fixed (PresentModeKHR* modesPtr = modes)
        {
            _context.KhrSurface.GetPhysicalDeviceSurfacePresentModes(
                _context.PhysicalDevice, _context.Surface, ref count, modesPtr);
        }

        if (Array.IndexOf(modes, PresentModeKHR.ImmediateKhr) >= 0)
        {
            return PresentModeKHR.ImmediateKhr;
        }

        if (Array.IndexOf(modes, PresentModeKHR.MailboxKhr) >= 0)
        {
            return PresentModeKHR.MailboxKhr;
        }

        Log.Warn("Vsync nejde vypnout — zařízení umí jen FIFO. Měření času framu bude měřit monitor.");
        return PresentModeKHR.FifoKhr;
    }

    /// <summary>
    /// Velikost swapchainu. Když ovladač vyplní <c>currentExtent</c>, je to závazné a
    /// naše přání se ignoruje; hodnota 0xFFFFFFFF znamená „rozhodni si sám".
    /// </summary>
    private static Extent2D ChooseExtent(SurfaceCapabilitiesKHR capabilities, int width, int height)
    {
        if (capabilities.CurrentExtent.Width != uint.MaxValue)
        {
            return capabilities.CurrentExtent;
        }

        return new Extent2D
        {
            Width = Math.Clamp(
                (uint)Math.Max(width, 1), capabilities.MinImageExtent.Width, capabilities.MaxImageExtent.Width),
            Height = Math.Clamp(
                (uint)Math.Max(height, 1), capabilities.MinImageExtent.Height, capabilities.MaxImageExtent.Height),
        };
    }

    private ImageView CreateView(Image image, Format format, ImageAspectFlags aspect)
    {
        var info = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = image,
            ViewType = ImageViewType.Type2D,
            Format = format,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = aspect,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1,
            },
        };

        VulkanContext.Check(
            _context.Vk.CreateImageView(_context.Device, &info, null, out ImageView view),
            "vkCreateImageView");

        return view;
    }

    /// <summary>
    /// Založí obraz a paměť pro něj. Sdílí to hloubka, vícevzorková barva i slitá hloubka.
    /// </summary>
    private (Image Image, DeviceMemory Memory) CreateImage(
        Extent2D extent, Format format, SampleCountFlags samples, ImageUsageFlags usage)
    {
        var imageInfo = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = format,
            Extent = new Extent3D(extent.Width, extent.Height, 1),
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = samples,
            Tiling = ImageTiling.Optimal,
            Usage = usage,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined,
        };

        VulkanContext.Check(
            _context.Vk.CreateImage(_context.Device, &imageInfo, null, out Image image), "vkCreateImage");

        _context.Vk.GetImageMemoryRequirements(_context.Device, image, out MemoryRequirements requirements);

        var allocate = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = requirements.Size,
            MemoryTypeIndex = _context.FindMemoryType(
                requirements.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit),
        };

        VulkanContext.Check(
            _context.Vk.AllocateMemory(_context.Device, &allocate, null, out DeviceMemory memory),
            "vkAllocateMemory");

        VulkanContext.Check(_context.Vk.BindImageMemory(_context.Device, image, memory, 0), "vkBindImageMemory");

        return (image, memory);
    }

    /// <summary>
    /// Založí cíle pro vyhlazení hran: vícevzorkovou barvu a jednovzorkovou kopii hloubky.
    /// </summary>
    /// <remarks>
    /// <para>Při jednom vzorku nedělá nic — kreslí se pak rovnou do obrazu swapchainu
    /// a hloubka se čte přímo z přílohy, jako to bylo předtím.</para>
    ///
    /// <para><b>Kopie hloubky je tu kvůli vodě.</b> Vícevzorkový obraz nejde číst obyčejným
    /// samplerem, a screen-space odraz hloubku potřebuje. Slévá se do ní na konci
    /// neprůhledného úseku.</para>
    /// </remarks>
    private void CreateMsaaTargets(Extent2D extent)
    {
        if (Samples == SampleCountFlags.Count1Bit)
        {
            return;
        }

        (_msaaColorImage, _msaaColorMemory) = CreateImage(
            extent, SceneColorFormat, Samples,
            ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransientAttachmentBit);

        _msaaColorView = CreateView(_msaaColorImage, SceneColorFormat, ImageAspectFlags.ColorBit);

        (_depthResolveImage, _depthResolveMemory) = CreateImage(
            extent, _context.DepthFormat, SampleCountFlags.Count1Bit,
            ImageUsageFlags.DepthStencilAttachmentBit | ImageUsageFlags.SampledBit);

        _depthResolveView = CreateView(_depthResolveImage, _context.DepthFormat, ImageAspectFlags.DepthBit);
    }

    private void CreateDepthBuffer(Extent2D extent)
    {
        var imageInfo = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = _context.DepthFormat,
            Extent = new Extent3D(extent.Width, extent.Height, 1),
            MipLevels = 1,
            ArrayLayers = 1,

            // Hloubka musí mít TÝŽ počet vzorků jako barva, jinak Vulkan pipeline odmítne.
            Samples = Samples,

            Tiling = ImageTiling.Optimal,

            // SampledBit přibyl kvůli vodě: screen-space odraz i lom potřebují vědět, jak
            // daleko leží to, co už je nakreslené, a to je přesně obsah hloubky. Bez toho
            // bit se z obrazu ve shaderu přečíst nedá vůbec.
            //
            // Při zapnutém vyhlazení se z ní stejně nečte přímo — voda bere slitou kopii —
            // ale bit nevadí a drží to jednu cestu pro oba případy.
            Usage = ImageUsageFlags.DepthStencilAttachmentBit | ImageUsageFlags.SampledBit,

            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined,
        };

        VulkanContext.Check(
            _context.Vk.CreateImage(_context.Device, &imageInfo, null, out _depthImage),
            "vkCreateImage (hloubka)");

        _context.Vk.GetImageMemoryRequirements(_context.Device, _depthImage, out MemoryRequirements requirements);

        var allocate = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = requirements.Size,
            MemoryTypeIndex = _context.FindMemoryType(
                requirements.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit),
        };

        VulkanContext.Check(
            _context.Vk.AllocateMemory(_context.Device, &allocate, null, out _depthMemory),
            "vkAllocateMemory (hloubka)");

        VulkanContext.Check(
            _context.Vk.BindImageMemory(_context.Device, _depthImage, _depthMemory, 0),
            "vkBindImageMemory (hloubka)");

        _depthView = CreateView(_depthImage, _context.DepthFormat, DepthAspect);
        _depthSampleView = CreateView(_depthImage, _context.DepthFormat, ImageAspectFlags.DepthBit);
    }

    /// <summary>
    /// Založí kopii scény, ze které si voda bere odraz a lom.
    /// </summary>
    /// <remarks>
    /// <para>Je to samostatný obraz, ne obraz swapchainu. Číst z obrazu, do kterého se
    /// zrovna kreslí, specifikace zakazuje: fragmenty vody by četly jednou původní pozadí
    /// a jednou už sebe, podle toho, v jakém pořadí je grafika stihla. Kopie tenhle spor
    /// odstraní za cenu jednoho <c>vkCmdCopyImage</c> na snímek.</para>
    ///
    /// <para>Formát je týž jako u swapchainu, aby kopie byla prostý přenos bez převodu.</para>
    /// </remarks>
    private void CreateSceneTarget(Extent2D extent)
    {
        (_hdrColorImage, _hdrColorMemory) = CreateImage(
            extent,
            SceneColorFormat,
            SampleCountFlags.Count1Bit,
            ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.SampledBit);
        _hdrColorView = CreateView(_hdrColorImage, SceneColorFormat, ImageAspectFlags.ColorBit);

        var imageInfo = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = SceneColorFormat,
            Extent = new Extent3D(extent.Width, extent.Height, 1),
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = ImageUsageFlags.TransferDstBit | ImageUsageFlags.SampledBit,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined,
        };

        VulkanContext.Check(
            _context.Vk.CreateImage(_context.Device, &imageInfo, null, out _sceneImage),
            "vkCreateImage (kopie scény)");

        _context.Vk.GetImageMemoryRequirements(_context.Device, _sceneImage, out MemoryRequirements requirements);

        var allocate = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = requirements.Size,
            MemoryTypeIndex = _context.FindMemoryType(
                requirements.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit),
        };

        VulkanContext.Check(
            _context.Vk.AllocateMemory(_context.Device, &allocate, null, out _sceneMemory),
            "vkAllocateMemory (kopie scény)");

        VulkanContext.Check(
            _context.Vk.BindImageMemory(_context.Device, _sceneImage, _sceneMemory, 0),
            "vkBindImageMemory (kopie scény)");

        _sceneView = CreateView(_sceneImage, SceneColorFormat, ImageAspectFlags.ColorBit);
    }

    /// <summary>
    /// Samplery pro kopii scény a pro hloubku.
    /// </summary>
    /// <remarks>
    /// Obojí má <c>ClampToEdge</c>. Raymarch se ptá i na místa mimo obrazovku a opakování
    /// by mu vrátilo obraz z protějšího okraje — v odrazu by se objevil kus scény, který
    /// tam nepatří. Okrajová hodnota je oproti tomu poznat a shader ji umí zahodit.
    /// </remarks>
    private void CreateSamplers()
    {
        Sampler Build(Filter filter)
        {
            var info = new SamplerCreateInfo
            {
                SType = StructureType.SamplerCreateInfo,
                MagFilter = filter,
                MinFilter = filter,
                MipmapMode = SamplerMipmapMode.Nearest,
                AddressModeU = SamplerAddressMode.ClampToEdge,
                AddressModeV = SamplerAddressMode.ClampToEdge,
                AddressModeW = SamplerAddressMode.ClampToEdge,
                AnisotropyEnable = false,
                BorderColor = BorderColor.FloatOpaqueBlack,
                CompareEnable = false,
                MinLod = 0f,
                MaxLod = 0f,
            };

            VulkanContext.Check(
                _context.Vk.CreateSampler(_context.Device, &info, null, out Sampler sampler),
                "vkCreateSampler");

            return sampler;
        }

        _sceneSampler = Build(Filter.Linear);
        _depthSampler = Build(Filter.Nearest);
    }

    private void DestroyResources()
    {
        Vk vk = _context.Vk;
        Device device = _context.Device;

        if (_msaaColorView.Handle != 0)
        {
            vk.DestroyImageView(device, _msaaColorView, null);
            _msaaColorView = default;
        }

        if (_msaaColorImage.Handle != 0)
        {
            vk.DestroyImage(device, _msaaColorImage, null);
            _msaaColorImage = default;
        }

        if (_msaaColorMemory.Handle != 0)
        {
            vk.FreeMemory(device, _msaaColorMemory, null);
            _msaaColorMemory = default;
        }

        if (_depthResolveView.Handle != 0)
        {
            vk.DestroyImageView(device, _depthResolveView, null);
            _depthResolveView = default;
        }

        if (_depthResolveImage.Handle != 0)
        {
            vk.DestroyImage(device, _depthResolveImage, null);
            _depthResolveImage = default;
        }

        if (_depthResolveMemory.Handle != 0)
        {
            vk.FreeMemory(device, _depthResolveMemory, null);
            _depthResolveMemory = default;
        }

        if (_sceneSampler.Handle != 0)
        {
            vk.DestroySampler(device, _sceneSampler, null);
            _sceneSampler = default;
        }

        if (_depthSampler.Handle != 0)
        {
            vk.DestroySampler(device, _depthSampler, null);
            _depthSampler = default;
        }

        if (_sceneView.Handle != 0)
        {
            vk.DestroyImageView(device, _sceneView, null);
            _sceneView = default;
        }

        if (_sceneImage.Handle != 0)
        {
            vk.DestroyImage(device, _sceneImage, null);
            _sceneImage = default;
        }

        if (_sceneMemory.Handle != 0)
        {
            vk.FreeMemory(device, _sceneMemory, null);
            _sceneMemory = default;
        }

        if (_hdrColorView.Handle != 0)
        {
            vk.DestroyImageView(device, _hdrColorView, null);
            _hdrColorView = default;
        }

        if (_hdrColorImage.Handle != 0)
        {
            vk.DestroyImage(device, _hdrColorImage, null);
            _hdrColorImage = default;
        }

        if (_hdrColorMemory.Handle != 0)
        {
            vk.FreeMemory(device, _hdrColorMemory, null);
            _hdrColorMemory = default;
        }

        if (_depthSampleView.Handle != 0)
        {
            vk.DestroyImageView(device, _depthSampleView, null);
            _depthSampleView = default;
        }

        if (_depthView.Handle != 0)
        {
            vk.DestroyImageView(device, _depthView, null);
            _depthView = default;
        }

        if (_depthImage.Handle != 0)
        {
            vk.DestroyImage(device, _depthImage, null);
            _depthImage = default;
        }

        if (_depthMemory.Handle != 0)
        {
            vk.FreeMemory(device, _depthMemory, null);
            _depthMemory = default;
        }

        foreach (ImageView view in _views)
        {
            if (view.Handle != 0)
            {
                vk.DestroyImageView(device, view, null);
            }
        }

        _views = [];

        // Obrazy samotné patří swapchainu, takže se neuvolňují — zmizí s ním.
        _images = [];

        if (_swapchain.Handle != 0)
        {
            _context.KhrSwapchain.DestroySwapchain(device, _swapchain, null);
            _swapchain = default;
        }
    }

    public void Dispose() => DestroyResources();
}
