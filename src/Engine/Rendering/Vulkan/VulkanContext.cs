using Silk.NET.Core;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using Tesseris.Engine.Core;

namespace Tesseris.Engine.Rendering.Vulkan;

/// <summary>
/// Vulkan instance, zařízení, fronta a paměť — tedy všechno, co žije po celou dobu běhu
/// a nemění se při změně velikosti okna. Co se mění, patří do <see cref="VulkanSwapchain"/>.
/// </summary>
public sealed unsafe class VulkanContext : IDisposable
{
    private VulkanDebug? _debug;

    private VulkanContext(Vk vk) => Vk = vk;

    public Vk Vk { get; }

    public Instance Instance { get; private set; }

    public SurfaceKHR Surface { get; private set; }

    public PhysicalDevice PhysicalDevice { get; private set; }

    public Device Device { get; private set; }

    /// <summary>
    /// Zásobník paměti zařízení. Buffery si z něj berou úseky místo vlastních alokací.
    /// </summary>
    public VulkanMemoryPool Memory { get; private set; } = null!;

    public Queue Queue { get; private set; }

    public uint QueueFamily { get; private set; }

    public KhrSurface KhrSurface { get; private set; } = null!;

    public KhrSwapchain KhrSwapchain { get; private set; } = null!;

    /// <summary>Jméno zařízení. Jde do logu i do overlaye místo dřívějšího <c>GL_RENDERER</c>.</summary>
    public string DeviceName { get; private set; } = "(neznámé)";

    /// <summary>Verze ovladače tak, jak se hlásí. U MoltenVK je to verze překladové vrstvy.</summary>
    public string DriverInfo { get; private set; } = "(neznámé)";

    /// <summary>Podporovaná verze Vulkanu na zařízení.</summary>
    public string ApiVersion { get; private set; } = "(neznámé)";

    /// <summary>
    /// Formát hloubkového bufferu. Nesmí se zadrátovat: na Apple GPU <b>není</b>
    /// <c>D24_UNORM_S8_UINT</c> podporovaný (ověřeno měřením), zatímco na PC je běžný.
    /// </summary>
    public Format DepthFormat { get; private set; } = Format.D32Sfloat;

    /// <summary>Běžíme přes MoltenVK, tedy nad Metalem, ne nad nativním Vulkanem?</summary>
    public bool IsPortability { get; private set; }

    /// <summary>Largest device-local heap reported by Vulkan; unified-memory Macs report their shared heap.</summary>
    public ulong DeviceLocalMemoryBytes { get; private set; }

    private SampleCountFlags _supportedSamples = SampleCountFlags.Count1Bit;

    /// <summary>Kolik samostatných bloků paměti smí existovat naráz. Viz VulkanBuffer.</summary>
    public uint MaxMemoryAllocations { get; private set; }

    /// <summary>
    /// Kolik bajtů push konstant zařízení unese.
    /// </summary>
    /// <remarks>
    /// Vulkan zaručuje jen 128 B, což chunky přesně vyčerpají (matice plus čtyři vektory).
    /// Stínové mapy potřebují ještě jednu matici navíc, takže se musí zeptat, kolik je
    /// doopravdy k dispozici — běžné karty dávají 256 B.
    /// </remarks>
    public uint MaxPushConstants { get; private set; }

    /// <summary>
    /// Nejvyšší povolená míra anizotropní filtrace, nebo 1 (tedy vypnuto), když ji
    /// zařízení neumí.
    /// </summary>
    /// <remarks>
    /// <para><b>Proč na tom u voxelové hry záleží.</b> Krajina se skoro vždycky prohlíží
    /// z ostrého úhlu — pohled klouže po zemi k obzoru. Textura se pak v jednom směru
    /// smršťuje mnohem víc než ve druhém a obyčejná mipmapa musí zvolit jedno číslo pro
    /// oba: buď rozmaže i směr, kde by detail byl vidět, nebo nechá druhý směr aliasovat.
    /// Ve hře to vypadalo tak, že vzdálený terén byl rozostřený a při chůzi se po něm
    /// vlnily vzory.</para>
    ///
    /// <para>Anizotropní filtrace vzorkuje podél toho směru, ve kterém je textura
    /// stlačená, a tenhle spor odstraní.</para>
    /// </remarks>
    public float MaxAnisotropy { get; private set; } = 1f;

    /// <summary>
    /// Kolik vzorků na pixel umí zařízení pro barvu i hloubku zároveň.
    /// </summary>
    /// <remarks>
    /// Bere se průnik obou masek: příloha barvy a hloubky musí mít v jednom průchodu
    /// <b>tentýž</b> počet vzorků, jinak Vulkan pipeline odmítne.
    /// </remarks>
    public SampleCountFlags MaxSamples { get; private set; } = SampleCountFlags.Count1Bit;

    /// <summary>
    /// Kolik nanosekund odpovídá jednomu tiku časové značky grafiky. Nula znamená, že
    /// zařízení značky neumí a měření po průchodech není k dispozici.
    /// </summary>
    public float TimestampPeriod { get; private set; }

    private CommandPool _immediatePool;
    private Fence _immediateFence;

    /// <summary>
    /// Postaví celý kontext. <see cref="VulkanLoader.Initialize"/> už musí být za námi.
    /// </summary>
    public static VulkanContext Create(
        VulkanSurfaceProvider surfaceProvider,
        string applicationName,
        int preferredSampleCount = 2)
    {
        ArgumentNullException.ThrowIfNull(surfaceProvider);

        Vk vk = VulkanLoader.Api;
        bool validation = VulkanDebug.DetectValidationLayer(vk);

        var context = new VulkanContext(vk);

        try
        {
            context.CreateInstance(surfaceProvider.RequiredInstanceExtensions, applicationName, validation);
            context._debug = VulkanDebug.Install(vk, context.Instance);
            context.CreateSurface(surfaceProvider.CreateSurface);
            context.PickPhysicalDevice(preferredSampleCount);
            context.CreateDevice();
            context.CreateImmediateSubmit();
        }
        catch
        {
            // Bez tohohle by po nezdaru uprostred stavby zustala viset instance i surface.
            context.Dispose();
            throw;
        }

        return context;
    }

    private void CreateInstance(string[] windowExtensions, string applicationName, bool validation)
    {
        var extensions = new List<string>(windowExtensions)
        {
            // Bez tohohle rozšíření a odpovídajícího flagu MoltenVK zařízení vůbec nevydá:
            // hlásí se jako "portability" ovladač, protože Vulkan nad Metalem nesplňuje
            // specifikaci do posledního detailu.
            "VK_KHR_portability_enumeration",

            // Potřeba pro dotaz na VK_KHR_portability_subset u zařízení.
            "VK_KHR_get_physical_device_properties2",

            ExtensionNameDebugUtils,
        };

        var layers = new List<string>();
        if (validation)
        {
            layers.Add(VulkanDebug.ValidationLayerName);
        }

        byte* appName = (byte*)SilkMarshal.StringToPtr(applicationName);
        byte* engineName = (byte*)SilkMarshal.StringToPtr("Tesseris");
        nint extensionsPtr = SilkMarshal.StringArrayToPtr(extensions);
        nint layersPtr = layers.Count > 0 ? SilkMarshal.StringArrayToPtr(layers) : 0;

        try
        {
            var appInfo = new ApplicationInfo
            {
                SType = StructureType.ApplicationInfo,
                PApplicationName = appName,
                ApplicationVersion = new Version32(1, 0, 0),
                PEngineName = engineName,
                EngineVersion = new Version32(1, 0, 0),

                // 1.3 kvůli dynamic rendering a synchronization2 — obojí je v jádře od 1.3
                // a ušetří render passy, framebuffery a starou podobu bariér.
                ApiVersion = Vk.Version13,
            };

            var info = new InstanceCreateInfo
            {
                SType = StructureType.InstanceCreateInfo,
                PApplicationInfo = &appInfo,
                EnabledExtensionCount = (uint)extensions.Count,
                PpEnabledExtensionNames = (byte**)extensionsPtr,
                EnabledLayerCount = (uint)layers.Count,
                PpEnabledLayerNames = layers.Count > 0 ? (byte**)layersPtr : null,
                Flags = InstanceCreateFlags.EnumeratePortabilityBitKhr,
            };

            Result result = Vk.CreateInstance(&info, null, out Instance instance);
            if (result != Result.Success)
            {
                throw new InvalidOperationException(
                    $"vkCreateInstance selhalo ({result}). Rozšíření: {string.Join(", ", extensions)}.");
            }

            Instance = instance;
        }
        finally
        {
            SilkMarshal.Free((nint)appName);
            SilkMarshal.Free((nint)engineName);
            SilkMarshal.Free(extensionsPtr);
            if (layersPtr != 0)
            {
                SilkMarshal.Free(layersPtr);
            }
        }

        if (!Vk.TryGetInstanceExtension(Instance, out KhrSurface khrSurface))
        {
            throw new InvalidOperationException("VK_KHR_surface se nepodařilo načíst.");
        }

        KhrSurface = khrSurface;
    }

    private const string ExtensionNameDebugUtils = "VK_EXT_debug_utils";

    private void CreateSurface(Func<nint, nint> createSurface)
    {
        nint handle = createSurface((nint)Instance.Handle);
        if (handle == 0)
        {
            throw new InvalidOperationException(
                "Okenní knihovna nevytvořila Vulkan surface. Na macOS to obvykle znamená, "
                + "že GLFW nenašlo MoltenVK.");
        }

        Surface = new SurfaceKHR((ulong)handle);
    }

    private void PickPhysicalDevice(int preferredSampleCount)
    {
        uint count = 0;
        Vk.EnumeratePhysicalDevices(Instance, ref count, null);

        if (count == 0)
        {
            throw new InvalidOperationException(
                "Žádné zařízení s podporou Vulkanu. Na macOS zkontroluj, že je nainstalované MoltenVK.");
        }

        var devices = new PhysicalDevice[count];
        fixed (PhysicalDevice* devicesPtr = devices)
        {
            Vk.EnumeratePhysicalDevices(Instance, ref count, devicesPtr);
        }

        PhysicalDevice? best = null;
        int bestScore = -1;

        foreach (PhysicalDevice candidate in devices)
        {
            if (!TryScoreDevice(candidate, out int score))
            {
                continue;
            }

            if (score > bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }

        if (best is null)
        {
            throw new InvalidOperationException(
                "Žádné zařízení nesplňuje požadavky (Vulkan 1.3, swapchain, fronta s grafikou i present).");
        }

        PhysicalDevice = best.Value;

        PhysicalDeviceProperties properties;
        Vk.GetPhysicalDeviceProperties(PhysicalDevice, &properties);

        DeviceName = SilkMarshal.PtrToString((nint)properties.DeviceName) ?? "(neznámé)";
        ApiVersion = FormatVersion(properties.ApiVersion);
        DriverInfo = FormatVersion(properties.DriverVersion);
        QueueFamily = FindQueueFamily(PhysicalDevice) ?? throw new InvalidOperationException("Fronta zmizela.");
        IsPortability = HasDeviceExtension(PhysicalDevice, "VK_KHR_portability_subset");
        DepthFormat = PickDepthFormat();

        MaxMemoryAllocations = properties.Limits.MaxMemoryAllocationCount;
        MaxPushConstants = properties.Limits.MaxPushConstantsSize;

        Log.Info($"Zařízení: {DeviceName}, Vulkan {ApiVersion}, ovladač {DriverInfo}, push konstanty {MaxPushConstants} B.");

        // Anizotropie je nepovinná vlastnost. Zařízení, které ji neumí, dostane 1 a sampler
        // ji pak nechá vypnutou — obraz bude jako dřív, jen se nic nerozbije.
        PhysicalDeviceFeatures supported;
        Vk.GetPhysicalDeviceFeatures(PhysicalDevice, &supported);

        MaxAnisotropy = supported.SamplerAnisotropy
            ? Math.Min(properties.Limits.MaxSamplerAnisotropy, 16f)
            : 1f;

        // Nejvyšší počet vzorků, který zvládne barva i hloubka. Strop je 4: osm vzorků
        // stojí dvakrát tolik paměti i propustnosti a rozdíl proti čtyřem je sotva znát.
        SampleCountFlags both = properties.Limits.FramebufferColorSampleCounts
            & properties.Limits.FramebufferDepthSampleCounts;
        _supportedSamples = both;

        preferredSampleCount = Math.Clamp(preferredSampleCount, 1, 4);
        MaxSamples = preferredSampleCount >= 4 && (both & SampleCountFlags.Count4Bit) != 0
            ? SampleCountFlags.Count4Bit
            : preferredSampleCount >= 2 && (both & SampleCountFlags.Count2Bit) != 0
                ? SampleCountFlags.Count2Bit
                : SampleCountFlags.Count1Bit;

        // VYHLAZENÍ HRAN SE DÁ OMEZIT PROMĚNNOU PROSTŘEDÍ.
        //
        // Čtyři vzorky znamenají čtyřnásobek práce na každý pixel, a to je zdaleka
        // nejdražší věc v celém kreslení. Přepnout se za běhu nedá — počet vzorků je
        // zapečený v každé pipeline — ale při startu ano, a to stačí k tomu, aby se dal
        // změřit jeho skutečný podíl na snímku.
        //
        //   TESSERIS_MSAA=0 nebo 1  → vypnuto
        //   TESSERIS_MSAA=2         → dva vzorky
        //   nenastaveno             → hodnota z grafického profilu (standardně 2x)
        string? requested = Environment.GetEnvironmentVariable("TESSERIS_MSAA");

        if (requested is "0" or "1")
        {
            MaxSamples = SampleCountFlags.Count1Bit;
        }
        else if (requested == "2" && (both & SampleCountFlags.Count2Bit) != 0)
        {
            MaxSamples = SampleCountFlags.Count2Bit;
        }

        Vk.GetPhysicalDeviceMemoryProperties(PhysicalDevice, out PhysicalDeviceMemoryProperties memory);
        for (int heap = 0; heap < memory.MemoryHeapCount; heap++)
        {
            MemoryHeap candidate = memory.MemoryHeaps[heap];
            if ((candidate.Flags & MemoryHeapFlags.DeviceLocalBit) != 0)
            {
                DeviceLocalMemoryBytes = Math.Max(DeviceLocalMemoryBytes, candidate.Size);
            }
        }

        // Časové značky grafiky. Bez nich se nedá změřit, který průchod kolik stojí —
        // čas hlavního vlákna o tom neříká nic, protože grafika běží nezávisle.
        TimestampPeriod = properties.Limits.TimestampComputeAndGraphics
            ? properties.Limits.TimestampPeriod
            : 0f;

        Log.Info(
            $"Anizotropie do {MaxAnisotropy}x, vyhlazení hran až {MaxSamples}, "
            + $"časové značky {(TimestampPeriod > 0f ? $"{TimestampPeriod} ns/tik" : "nejsou")}.");

        Log.Info($"Hloubkový formát {DepthFormat}, fronta {QueueFamily}, portability: {IsPortability}.");
        Log.Info($"Strop paměťových alokací: {MaxMemoryAllocations}.");
        Log.Info("Paměťové typy zařízení:");
        LogMemoryTypes();
    }

    /// <summary>Changes MSAA before swapchain/pipelines are created.</summary>
    public void ConfigureSampleCount(int preferredSampleCount)
    {
        string? overrideValue = Environment.GetEnvironmentVariable("TESSERIS_MSAA");
        if (int.TryParse(overrideValue, out int overriddenSamples) && overriddenSamples is 0 or 1 or 2 or 4)
            preferredSampleCount = Math.Max(1, overriddenSamples);

        preferredSampleCount = Math.Clamp(preferredSampleCount, 1, 4);
        MaxSamples = preferredSampleCount >= 4 && (_supportedSamples & SampleCountFlags.Count4Bit) != 0
            ? SampleCountFlags.Count4Bit
            : preferredSampleCount >= 2 && (_supportedSamples & SampleCountFlags.Count2Bit) != 0
                ? SampleCountFlags.Count2Bit
                : SampleCountFlags.Count1Bit;
    }

    private bool TryScoreDevice(PhysicalDevice device, out int score)
    {
        score = 0;

        PhysicalDeviceProperties properties;
        Vk.GetPhysicalDeviceProperties(device, &properties);

        // Dynamic rendering a synchronization2 jsou v jádře od 1.3; na nich stojí celý
        // renderer, takže starší zařízení nemá smysl přijímat.
        if (properties.ApiVersion < Vk.Version13)
        {
            return false;
        }

        if (!HasDeviceExtension(device, KhrSwapchain.ExtensionName))
        {
            return false;
        }

        if (FindQueueFamily(device) is null)
        {
            return false;
        }

        var vulkan13 = new PhysicalDeviceVulkan13Features { SType = StructureType.PhysicalDeviceVulkan13Features };
        var features = new PhysicalDeviceFeatures2
        {
            SType = StructureType.PhysicalDeviceFeatures2,
            PNext = &vulkan13,
        };

        Vk.GetPhysicalDeviceFeatures2(device, &features);

        // ShaderDemoteToHelperInvocation je tu kvůli `discard` ve fragment shaderech.
        // SPIR-V 1.6, které si vynucuje --target-env vulkan1.3, zrušilo OpKill, takže
        // glslang přeloží `discard` na OpDemoteToHelperInvocation — a ten se bez zapnuté
        // vlastnosti použít nesmí. Vulkan 1.3 ji vyžaduje po všech zařízeních, takže
        // podmínka nikoho nevyřadí; je tu proto, aby se případný nesoulad projevil
        // při výběru zařízení, ne až padlým shaderem.
        if (!vulkan13.DynamicRendering || !vulkan13.Synchronization2 || !vulkan13.ShaderDemoteToHelperInvocation)
        {
            return false;
        }

        // Samostatná grafika je lepší než integrovaná — až na Apple Silicon, kde je
        // integrovaná jediná možnost a zároveň sdílí paměť s CPU.
        score = properties.DeviceType switch
        {
            PhysicalDeviceType.DiscreteGpu => 1000,
            PhysicalDeviceType.IntegratedGpu => 500,
            _ => 100,
        };

        return true;
    }

    private uint? FindQueueFamily(PhysicalDevice device)
    {
        uint count = 0;
        Vk.GetPhysicalDeviceQueueFamilyProperties(device, ref count, null);

        var families = new QueueFamilyProperties[count];
        fixed (QueueFamilyProperties* familiesPtr = families)
        {
            Vk.GetPhysicalDeviceQueueFamilyProperties(device, ref count, familiesPtr);
        }

        // Hledá se jedna fronta, která umí grafiku i present. Kdyby taková nebyla, musely by
        // se buffery mezi frontami předávat a celá synchronizace by ztloustla — na cílových
        // zařízeních ale existuje vždycky (ověřeno: na Apple M5 ji splňují všechny čtyři).
        for (uint i = 0; i < count; i++)
        {
            if ((families[i].QueueFlags & QueueFlags.GraphicsBit) == 0)
            {
                continue;
            }

            KhrSurface.GetPhysicalDeviceSurfaceSupport(device, i, Surface, out Bool32 present);
            if (present)
            {
                return i;
            }
        }

        return null;
    }

    private bool HasDeviceExtension(PhysicalDevice device, string name)
    {
        uint count = 0;
        Vk.EnumerateDeviceExtensionProperties(device, (byte*)null, ref count, null);

        var extensions = new ExtensionProperties[count];
        fixed (ExtensionProperties* extensionsPtr = extensions)
        {
            Vk.EnumerateDeviceExtensionProperties(device, (byte*)null, ref count, extensionsPtr);
        }

        foreach (ExtensionProperties extension in extensions)
        {
            if (SilkMarshal.PtrToString((nint)extension.ExtensionName) == name)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Vybere hloubkový formát podle toho, co zařízení umí. Pořadí je od nejlevnějšího:
    /// bez stencilu, protože ho renderer nepoužívá.
    /// </summary>
    private Format PickDepthFormat()
    {
        foreach (Format candidate in (ReadOnlySpan<Format>)[Format.D32Sfloat, Format.D32SfloatS8Uint, Format.D24UnormS8Uint])
        {
            Vk.GetPhysicalDeviceFormatProperties(PhysicalDevice, candidate, out FormatProperties properties);
            if ((properties.OptimalTilingFeatures & FormatFeatureFlags.DepthStencilAttachmentBit) != 0)
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("Zařízení neumí žádný použitelný hloubkový formát.");
    }

    private void CreateDevice()
    {
        float priority = 1f;
        var queueInfo = new DeviceQueueCreateInfo
        {
            SType = StructureType.DeviceQueueCreateInfo,
            QueueFamilyIndex = QueueFamily,
            QueueCount = 1,
            PQueuePriorities = &priority,
        };

        var extensions = new List<string> { KhrSwapchain.ExtensionName };

        // Specifikace je tady kategorická: když zařízení hlásí VK_KHR_portability_subset,
        // MUSÍ se při vytvoření zařízení zapnout. Bez toho je chování nedefinované.
        if (IsPortability)
        {
            extensions.Add("VK_KHR_portability_subset");
        }

        var vulkan13 = new PhysicalDeviceVulkan13Features
        {
            SType = StructureType.PhysicalDeviceVulkan13Features,
            DynamicRendering = true,
            Synchronization2 = true,

            // Nutné kvůli `discard` — viz zdůvodnění u kontroly vhodnosti zařízení.
            ShaderDemoteToHelperInvocation = true,
        };

        var features = new PhysicalDeviceFeatures2
        {
            SType = StructureType.PhysicalDeviceFeatures2,
            PNext = &vulkan13,

            // Anizotropní filtrace se musí zapnout i tady, ne jen v sampleru. Bez toho by
            // sampler s AnisotropyEnable byl porušením specifikace — a ovladači to sice
            // často projde, ale validační vrstva to hlásí jako chybu.
            Features = new PhysicalDeviceFeatures { SamplerAnisotropy = MaxAnisotropy > 1f },
        };

        nint extensionsPtr = SilkMarshal.StringArrayToPtr(extensions);

        try
        {
            var info = new DeviceCreateInfo
            {
                SType = StructureType.DeviceCreateInfo,
                PNext = &features,
                QueueCreateInfoCount = 1,
                PQueueCreateInfos = &queueInfo,
                EnabledExtensionCount = (uint)extensions.Count,
                PpEnabledExtensionNames = (byte**)extensionsPtr,
            };

            Result result = Vk.CreateDevice(PhysicalDevice, &info, null, out Device device);
            if (result != Result.Success)
            {
                throw new InvalidOperationException($"vkCreateDevice selhalo ({result}).");
            }

            Device = device;
            Memory = new VulkanMemoryPool(this);
        }
        finally
        {
            SilkMarshal.Free(extensionsPtr);
        }

        Vk.GetDeviceQueue(Device, QueueFamily, 0, out Queue queue);
        Queue = queue;

        if (!Vk.TryGetDeviceExtension(Instance, Device, out KhrSwapchain khrSwapchain))
        {
            throw new InvalidOperationException("VK_KHR_swapchain se nepodařilo načíst.");
        }

        KhrSwapchain = khrSwapchain;
    }

    /// <summary>
    /// Fronta a plot pro jednorázové převody — nahrání textury, změna layoutu obrazu.
    /// Děje se to jen při startu, takže se čeká na dokončení a nic se nekomplikuje.
    /// </summary>
    private void CreateImmediateSubmit()
    {
        var poolInfo = new CommandPoolCreateInfo
        {
            SType = StructureType.CommandPoolCreateInfo,
            QueueFamilyIndex = QueueFamily,
            Flags = CommandPoolCreateFlags.TransientBit | CommandPoolCreateFlags.ResetCommandBufferBit,
        };

        Check(Vk.CreateCommandPool(Device, &poolInfo, null, out _immediatePool), "vkCreateCommandPool");

        var fenceInfo = new FenceCreateInfo { SType = StructureType.FenceCreateInfo };
        Check(Vk.CreateFence(Device, &fenceInfo, null, out _immediateFence), "vkCreateFence");
    }

    /// <summary>
    /// Nahraje a počká. Používá se na nahrání textur a na první přechody layoutů, tedy
    /// na věci, které se dějí jednou při startu a kde na propustnosti nezáleží.
    /// </summary>
    public void ImmediateSubmit(Action<CommandBuffer> record)
    {
        ArgumentNullException.ThrowIfNull(record);

        var allocateInfo = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = _immediatePool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = 1,
        };

        Check(Vk.AllocateCommandBuffers(Device, &allocateInfo, out CommandBuffer commandBuffer), "vkAllocateCommandBuffers");

        var beginInfo = new CommandBufferBeginInfo
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
        };

        Check(Vk.BeginCommandBuffer(commandBuffer, &beginInfo), "vkBeginCommandBuffer");
        record(commandBuffer);
        Check(Vk.EndCommandBuffer(commandBuffer), "vkEndCommandBuffer");

        var submitInfo = new SubmitInfo
        {
            SType = StructureType.SubmitInfo,
            CommandBufferCount = 1,
            PCommandBuffers = &commandBuffer,
        };

        Check(Vk.QueueSubmit(Queue, 1, &submitInfo, _immediateFence), "vkQueueSubmit");

        Fence fence = _immediateFence;
        Check(Vk.WaitForFences(Device, 1, &fence, true, ulong.MaxValue), "vkWaitForFences");
        Check(Vk.ResetFences(Device, 1, &fence), "vkResetFences");

        Vk.FreeCommandBuffers(Device, _immediatePool, 1, &commandBuffer);
    }

    /// <summary>
    /// Najde index paměťového typu, který splňuje požadované vlastnosti a je povolený maskou
    /// z <c>vkGetBufferMemoryRequirements</c>.
    ///
    /// Na Apple Silicon je paměť sdílená s CPU, takže existuje typ, který je zároveň
    /// <c>DEVICE_LOCAL</c> i <c>HOST_VISIBLE</c> (ověřeno měřením) — geometrie se tam dá
    /// zapsat rovnou a staging buffery nejsou potřeba.
    /// </summary>
    public uint FindMemoryType(uint typeBits, MemoryPropertyFlags required)
    {
        if (TryFindMemoryType(typeBits, required, out uint index))
        {
            return index;
        }

        throw new InvalidOperationException($"Není paměťový typ s vlastnostmi {required}.");
    }

    /// <summary>
    /// Totéž, ale bez výjimky. Používá se, když existuje druhá volba — typicky „nejdřív
    /// zkus paměť grafiky, a když tam není místo, vezmi systémovou".
    /// </summary>
    public bool TryFindMemoryType(uint typeBits, MemoryPropertyFlags required, out uint index)
    {
        Vk.GetPhysicalDeviceMemoryProperties(PhysicalDevice, out PhysicalDeviceMemoryProperties properties);

        for (uint i = 0; i < properties.MemoryTypeCount; i++)
        {
            bool allowed = (typeBits & (1u << (int)i)) != 0;
            bool suitable = (properties.MemoryTypes[(int)i].PropertyFlags & required) == required;

            if (allowed && suitable)
            {
                index = i;
                return true;
            }
        }

        index = 0;
        return false;
    }

    /// <summary>
    /// Vypíše, jaké paměťové typy zařízení nabízí.
    ///
    /// <para>Není to ozdoba. Volba paměťového typu rozhoduje o tom, jestli geometrie leží
    /// <b>v paměti grafiky, nebo v systémové RAM za sběrnicí PCIe</b>, a to je rozdíl řádu
    /// v propustnosti. Pouhé „HOST_VISIBLE | HOST_COHERENT" na samostatné grafice vybere
    /// systémovou paměť, protože ta je v seznamu dřív — a pozná se to jedině takhle.</para>
    /// </summary>
    public void LogMemoryTypes()
    {
        Vk.GetPhysicalDeviceMemoryProperties(PhysicalDevice, out PhysicalDeviceMemoryProperties properties);

        for (uint i = 0; i < properties.MemoryTypeCount; i++)
        {
            MemoryType type = properties.MemoryTypes[(int)i];
            ulong heapSize = properties.MemoryHeaps[(int)type.HeapIndex].Size;

            Log.Info(
                $"  typ {i}: {type.PropertyFlags} (halda {type.HeapIndex}, {heapSize / (1024 * 1024)} MB)");
        }
    }

    /// <summary>Vyhodí výjimku s názvem volání, když Vulkan vrátí chybu.</summary>
    public static void Check(Result result, string call)
    {
        if (result != Result.Success)
        {
            throw new InvalidOperationException($"{call} selhalo: {result}.");
        }
    }

    private static string FormatVersion(uint version) =>
        $"{version >> 22}.{(version >> 12) & 0x3FF}.{version & 0xFFF}";

    public void Dispose()
    {
        if (Device.Handle != 0)
        {
            Vk.DeviceWaitIdle(Device);

            // Paměťové bloky až po tom, co všechno ostatní vrátilo své úseky.
            Memory?.Dispose();

            if (_immediateFence.Handle != 0)
            {
                Vk.DestroyFence(Device, _immediateFence, null);
                _immediateFence = default;
            }

            if (_immediatePool.Handle != 0)
            {
                Vk.DestroyCommandPool(Device, _immediatePool, null);
                _immediatePool = default;
            }

            KhrSwapchain?.Dispose();
            Vk.DestroyDevice(Device, null);
            Device = default;
        }

        if (Surface.Handle != 0)
        {
            KhrSurface.DestroySurface(Instance, Surface, null);
            Surface = default;
        }

        _debug?.Dispose();
        KhrSurface?.Dispose();

        if (Instance.Handle != 0)
        {
            Vk.DestroyInstance(Instance, null);
            Instance = default;
        }
    }
}
