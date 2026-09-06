using System.Reflection;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;

namespace Tesseris.Engine.Rendering.Vulkan;

/// <summary>Jak se má stupeň míchání a hloubky chovat. Odpovídá průchodům původního rendereru.</summary>
public enum BlendMode
{
    /// <summary>Neprůhledné: zápis do hloubky, bez míchání.</summary>
    Opaque,

    /// <summary>Průhledné: míchání podle alfy, bez zápisu do hloubky.</summary>
    AlphaBlend,

    /// <summary>
    /// Výřez: zápis do hloubky jako u neprůhledného, ale shader smí fragment zahodit.
    ///
    /// <para><b>Tohle není totéž co průhlednost a rozdíl je zásadní pro výkon.</b> Tráva
    /// a listí jsou buď plné, nebo úplně díra — nic mezi tím. Kreslit je s mícháním
    /// znamená vypnout zápis do hloubky, takže se stébla navzájem nezakrývají a v louce
    /// se stínují stovky ploch přes sebe. Se zápisem do hloubky se to zahodí hned
    /// a přední stéblo ta za sebou schová.</para>
    ///
    /// <para>Míchání se naopak nechá vypnuté: u výřezu není co míchat.</para>
    /// </summary>
    Cutout,

    /// <summary>Overlay: bez hloubkového testu i zápisu, bez míchání.</summary>
    Overlay,

    /// <summary>
    /// Aditivní: to, co shader vydá, se k obrazu PŘIČTE. Bez hloubky, jako Overlay.
    /// </summary>
    /// <remarks>
    /// Pro záři a jiné světelné příspěvky, které nic nezakrývají, jen přidávají. Bloom
    /// tudy přičítá rozmazaná světlá místa zpátky do hotového obrazu.
    /// </remarks>
    Add,
}

/// <summary>
/// Grafický pipeline.
///
/// <para>
/// Tohle je největší rozdíl proti OpenGL. Tam se stav nastavoval za běhu jednotlivými
/// voláními (<c>glEnable(GL_BLEND)</c>, <c>glDepthMask</c>, <c>glCullFace</c>) a ovladač
/// si výslednou kombinaci přeložil, když bylo potřeba. Vulkan chce celou kombinaci předem
/// v jednom objektu. Kde měl OpenGL renderer tři průchody lišící se pár přepínači, má
/// vulkanský tři pipeline.
/// </para>
/// </summary>
public sealed unsafe class VulkanPipeline : IDisposable
{
    private readonly VulkanContext _context;

    private VulkanPipeline(VulkanContext context) => _context = context;

    public Pipeline Handle { get; private set; }

    public PipelineLayout Layout { get; private set; }

    /// <summary>Kolik bajtů push konstant tenhle pipeline přijímá.</summary>
    public uint PushConstantBytes { get; private set; }

    public DescriptorSetLayout DescriptorSetLayout { get; private set; }

    /// <summary>
    /// Sestaví pipeline.
    /// </summary>
    /// <param name="vertexAttributes">
    /// Popis atributů vrcholu. Formát musí sedět na <c>layout(location = …)</c> v shaderu.
    /// </param>
    /// <param name="pushConstantSize">
    /// Velikost bloku push konstant v bajtech. Nahrazuje uniformy z OpenGL verze —
    /// na Apple M5 je strop 4096 B (ověřeno), takže se do nich vejde všechno per-frame
    /// i per-draw a nejsou potřeba uniform buffery.
    /// </param>
    /// <param name="frontFace">
    /// Které navíjení je přední.
    ///
    /// <para>
    /// Zůstává <b>proti směru hodinových ručiček</b>, tedy stejně jako mělo OpenGL. Úvaha
    /// říká něco jiného — <see cref="VulkanClip"/> obrací osu Y, což orientaci trojúhelníku
    /// překlápí, takže by se čekalo navíjení po směru. Zkouška ale ukázala opak a rozhodl
    /// obraz, ne úvaha:
    /// </para>
    /// <list type="bullet">
    ///   <item>proti směru: rozdíl scény s cullingem a bez něj <b>0,03 %</b>, terén celistvý,</item>
    ///   <item>po směru: <b>5,89 %</b>, terén průhledný a vidět dovnitř.</item>
    /// </list>
    /// <para>
    /// Ověřeno na MoltenVK; jestli si tam něco přidává vlastní překlopení, se odsud
    /// rozhodnout nedá. Rozhodčím je selftest — když na jiné platformě vyskočí rozdíl
    /// cullingu nahoru, přehodí se tenhle parametr a nic jiného.
    /// </para>
    /// </param>
    /// <param name="instanceStride">
    /// Kolik bajtů zabírá jeden záznam per-instance dat. Nula znamená bez instancingu.
    /// </param>
    /// <remarks>
    /// <para><b>Proč to tu přibylo.</b> Do téhle chvíle uměla továrna právě jednu vazbu
    /// s napevno <c>InputRate.Vertex</c>, takže se instancované kreslení nedalo zapnout
    /// vůbec — všech deset volání kreslení v projektu mělo <c>instanceCount = 1</c>.
    /// Bez druhé vazby nejde splnit cíl „20 000 itemů, draw cally v jednotkách" (pravidlo 6.7).</para>
    ///
    /// <para><b>Jak se to používá.</b> Atributy se předávají v jednom poli
    /// <paramref name="vertexAttributes"/> a každý si nese svoje <c>Binding</c>: nula pro data
    /// vrcholu, jedna pro data instance. Vazba 1 se ohlásí jen tehdy, když je
    /// <paramref name="instanceStride"/> nenulový — jinak by validační vrstva hlásila
    /// deklarovanou, ale nepoužitou vazbu.</para>
    ///
    /// <para>Stávající volající se neměnili: bez toho parametru se chová přesně jako dřív.</para>
    /// </remarks>
    public static VulkanPipeline Create(
        VulkanContext context,
        VulkanSwapchain swapchain,
        Assembly shaderAssembly,
        string vertexShader,
        string fragmentShader,
        uint vertexStride,
        VertexInputAttributeDescription[] vertexAttributes,
        uint pushConstantSize,
        BlendMode blendMode,
        CullModeFlags cullMode,
        FrontFace frontFace = FrontFace.CounterClockwise,
        uint textureCount = 1,
        PrimitiveTopology topology = PrimitiveTopology.TriangleList,
        bool depthOnly = false,
        Format? colorFormat = null,
        SampleCountFlags? rasterizationSamples = null,
        bool hasDepthAttachment = true,
        uint instanceStride = 0)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(swapchain);
        ArgumentNullException.ThrowIfNull(vertexAttributes);

        var pipeline = new VulkanPipeline(context) { PushConstantBytes = pushConstantSize };
        Vk vk = context.Vk;

        ShaderModule vertexModule = SpirV.LoadModule(context, shaderAssembly, vertexShader);
        ShaderModule fragmentModule = SpirV.LoadModule(context, shaderAssembly, fragmentShader);

        byte* entryPoint = (byte*)SilkMarshal.StringToPtr("main");

        try
        {
            pipeline.CreateDescriptorSetLayout(textureCount);
            pipeline.CreateLayout(pushConstantSize);

            var stages = stackalloc PipelineShaderStageCreateInfo[2];
            stages[0] = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.VertexBit,
                Module = vertexModule,
                PName = entryPoint,
            };
            stages[1] = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.FragmentBit,
                Module = fragmentModule,
                PName = entryPoint,
            };

            // Dvě vazby: nula nese data vrcholu, jedna data instance. Druhá se ohlásí jen
            // tehdy, když se instancing opravdu používá — deklarovaná, ale nepoužitá vazba
            // je pro validační vrstvu chyba.
            var bindings = stackalloc VertexInputBindingDescription[2];
            bindings[0] = new VertexInputBindingDescription
            {
                Binding = 0,
                Stride = vertexStride,
                InputRate = VertexInputRate.Vertex,
            };
            bindings[1] = new VertexInputBindingDescription
            {
                Binding = 1,
                Stride = instanceStride,

                // TOHLE JE CELÝ INSTANCING. PerInstance znamená, že se atribut posune až
                // s další instancí, ne s dalším vrcholem — proto jedna geometrie a tisíce
                // poloh stojí jeden draw call.
                InputRate = VertexInputRate.Instance,
            };

            // Prázdné pole atributů znamená pipeline BEZ vertex bufferu — vrcholy si shader
            // vyrobí sám z gl_VertexIndex. Používá to obloha: fullscreen trojúhelník nemá
            // co načítat z paměti a vázat mu prázdný buffer by byla zbytečná obřadnost.
            //
            // Vazba se v tom případě musí ohlásit jako NULOVÁ, ne jako jedna s nulovým
            // krokem — validační vrstva druhou variantu hlásí jako chybu.
            bool hasVertexBuffer = vertexAttributes.Length > 0;
            bool hasInstanceBuffer = hasVertexBuffer && instanceStride > 0;

            fixed (VertexInputAttributeDescription* attributesPtr = vertexAttributes)
            {
                var vertexInput = new PipelineVertexInputStateCreateInfo
                {
                    SType = StructureType.PipelineVertexInputStateCreateInfo,
                    VertexBindingDescriptionCount = hasVertexBuffer ? (hasInstanceBuffer ? 2u : 1u) : 0u,
                    PVertexBindingDescriptions = hasVertexBuffer ? bindings : null,
                    VertexAttributeDescriptionCount = (uint)vertexAttributes.Length,
                    PVertexAttributeDescriptions = hasVertexBuffer ? attributesPtr : null,
                };

                var assembly = new PipelineInputAssemblyStateCreateInfo
                {
                    SType = StructureType.PipelineInputAssemblyStateCreateInfo,
                    Topology = topology,
                };

                // Viewport i scissor jsou dynamicke: pri zmene velikosti okna by se jinak
                // musely znovu stavet vsechny pipeline.
                var viewportState = new PipelineViewportStateCreateInfo
                {
                    SType = StructureType.PipelineViewportStateCreateInfo,
                    ViewportCount = 1,
                    ScissorCount = 1,
                };

                var rasterizer = new PipelineRasterizationStateCreateInfo
                {
                    SType = StructureType.PipelineRasterizationStateCreateInfo,
                    PolygonMode = PolygonMode.Fill,
                    LineWidth = 1f,
                    CullMode = cullMode,
                    FrontFace = frontFace,
                    DepthClampEnable = false,
                    RasterizerDiscardEnable = false,
                    DepthBiasEnable = false,
                };

                // Počet vzorků MUSÍ sedět na přílohy, jinak vytvoření pipeline selže.
                // Bere se proto ze swapchainu, který o vyhlazení rozhoduje.
                var multisample = new PipelineMultisampleStateCreateInfo
                {
                    SType = StructureType.PipelineMultisampleStateCreateInfo,
                    // Stínová mapa se kreslí do vlastního jednovzorkového obrazu, ne do
                    // swapchainu — vyhlazování hran u ní nemá smysl, ukládá se do ní hloubka.
                    RasterizationSamples = depthOnly
                        ? SampleCountFlags.Count1Bit
                        : rasterizationSamples ?? swapchain.Samples,
                };

                PipelineDepthStencilStateCreateInfo depthStencil = DepthState(blendMode);
                PipelineColorBlendAttachmentState blendAttachment = BlendState(blendMode);

                var colorBlend = new PipelineColorBlendStateCreateInfo
                {
                    SType = StructureType.PipelineColorBlendStateCreateInfo,
                    AttachmentCount = depthOnly ? 0u : 1u,
                    PAttachments = depthOnly ? null : &blendAttachment,
                };

                var dynamicStates = stackalloc DynamicState[2];
                dynamicStates[0] = DynamicState.Viewport;
                dynamicStates[1] = DynamicState.Scissor;

                var dynamicState = new PipelineDynamicStateCreateInfo
                {
                    SType = StructureType.PipelineDynamicStateCreateInfo,
                    DynamicStateCount = 2,
                    PDynamicStates = dynamicStates,
                };

                // Dynamic rendering: misto VkRenderPass a VkFramebuffer se formaty priloh
                // uvedou primo tady. Setri to dva objekty, ktere by se musely predelavat
                // pri kazde zmene velikosti okna.
                Format resolvedColorFormat = colorFormat ?? swapchain.ColorFormat;
                var renderingInfo = new PipelineRenderingCreateInfo
                {
                    SType = StructureType.PipelineRenderingCreateInfo,

                    // HLOUBKOVÝ PRŮCHOD NEMÁ BARVU. Do stínové mapy se zapisuje jen
                    // vzdálenost od slunce; barva by byla jen práce navíc, kterou nikdo
                    // nepřečte.
                    ColorAttachmentCount = depthOnly ? 0u : 1u,
                    PColorAttachmentFormats = depthOnly ? null : &resolvedColorFormat,
                    DepthAttachmentFormat = hasDepthAttachment ? context.DepthFormat : Format.Undefined,
                };

                var info = new GraphicsPipelineCreateInfo
                {
                    SType = StructureType.GraphicsPipelineCreateInfo,
                    PNext = &renderingInfo,
                    StageCount = 2,
                    PStages = stages,
                    PVertexInputState = &vertexInput,
                    PInputAssemblyState = &assembly,
                    PViewportState = &viewportState,
                    PRasterizationState = &rasterizer,
                    PMultisampleState = &multisample,
                    PDepthStencilState = &depthStencil,
                    PColorBlendState = &colorBlend,
                    PDynamicState = &dynamicState,
                    Layout = pipeline.Layout,
                    RenderPass = default,
                    Subpass = 0,
                };

                VulkanContext.Check(
                    vk.CreateGraphicsPipelines(context.Device, default, 1, &info, null, out Pipeline handle),
                    $"vkCreateGraphicsPipelines ({vertexShader})");

                pipeline.Handle = handle;
            }
        }
        finally
        {
            SilkMarshal.Free((nint)entryPoint);

            // Moduly uz nejsou potreba, jakmile je pipeline hotovy.
            vk.DestroyShaderModule(context.Device, vertexModule, null);
            vk.DestroyShaderModule(context.Device, fragmentModule, null);
        }

        return pipeline;
    }

    private static PipelineDepthStencilStateCreateInfo DepthState(BlendMode mode) => mode switch
    {
        // Neprůhledný průchod a výřez: test i zápis. Výřez se od neprůhledného liší jen
        // tím, že jeho shader smí fragment zahodit.
        BlendMode.Opaque or BlendMode.Cutout => new PipelineDepthStencilStateCreateInfo
        {
            SType = StructureType.PipelineDepthStencilStateCreateInfo,
            DepthTestEnable = true,
            DepthWriteEnable = true,
            DepthCompareOp = CompareOp.Less,
        },

        // Průhledný průchod: testuje se, ale nezapisuje — jinak by se skla navzájem ořezávala.
        BlendMode.AlphaBlend => new PipelineDepthStencilStateCreateInfo
        {
            SType = StructureType.PipelineDepthStencilStateCreateInfo,
            DepthTestEnable = true,
            DepthWriteEnable = false,
            DepthCompareOp = CompareOp.Less,
        },

        // Overlay patří nad scénu, takže hloubka úplně mimo hru.
        _ => new PipelineDepthStencilStateCreateInfo
        {
            SType = StructureType.PipelineDepthStencilStateCreateInfo,
            DepthTestEnable = false,
            DepthWriteEnable = false,
            DepthCompareOp = CompareOp.Always,
        },
    };

    private static PipelineColorBlendAttachmentState BlendState(BlendMode mode)
    {
        const ColorComponentFlags all = ColorComponentFlags.RBit
            | ColorComponentFlags.GBit
            | ColorComponentFlags.BBit
            | ColorComponentFlags.ABit;

        if (mode == BlendMode.Add)
        {
            // Zdroj i cíl plnou vahou: výsledek = to, co bylo, plus to, co shader vydal.
            return new PipelineColorBlendAttachmentState
            {
                ColorWriteMask = all,
                BlendEnable = true,
                SrcColorBlendFactor = BlendFactor.One,
                DstColorBlendFactor = BlendFactor.One,
                ColorBlendOp = BlendOp.Add,
                SrcAlphaBlendFactor = BlendFactor.Zero,
                DstAlphaBlendFactor = BlendFactor.One,
                AlphaBlendOp = BlendOp.Add,
            };
        }

        if (mode != BlendMode.AlphaBlend)
        {
            return new PipelineColorBlendAttachmentState { ColorWriteMask = all, BlendEnable = false };
        }

        return new PipelineColorBlendAttachmentState
        {
            ColorWriteMask = all,
            BlendEnable = true,
            SrcColorBlendFactor = BlendFactor.SrcAlpha,
            DstColorBlendFactor = BlendFactor.OneMinusSrcAlpha,
            ColorBlendOp = BlendOp.Add,
            SrcAlphaBlendFactor = BlendFactor.One,
            DstAlphaBlendFactor = BlendFactor.Zero,
            AlphaBlendOp = BlendOp.Add,
        };
    }

    /// <summary>
    /// Rozvržení descriptor setu: <paramref name="textureCount"/> textur ve fragment
    /// shaderu na vazbách 0 až N−1.
    /// </summary>
    /// <remarks>
    /// Skoro všechno si vystačí s jednou (atlas bloků, písmo). Výjimkou je voda, která
    /// k atlasu potřebuje ještě kopii scény a hloubku pro screen-space odraz a lom.
    /// </remarks>
    private void CreateDescriptorSetLayout(uint textureCount)
    {
        var bindings = stackalloc DescriptorSetLayoutBinding[(int)textureCount];

        for (uint i = 0; i < textureCount; i++)
        {
            bindings[i] = new DescriptorSetLayoutBinding
            {
                Binding = i,
                DescriptorType = DescriptorType.CombinedImageSampler,
                DescriptorCount = 1,
                StageFlags = ShaderStageFlags.FragmentBit,
            };
        }

        var info = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = textureCount,
            PBindings = bindings,
        };

        VulkanContext.Check(
            _context.Vk.CreateDescriptorSetLayout(_context.Device, &info, null, out DescriptorSetLayout layout),
            "vkCreateDescriptorSetLayout");

        DescriptorSetLayout = layout;
    }

    private void CreateLayout(uint pushConstantSize)
    {
        DescriptorSetLayout setLayout = DescriptorSetLayout;

        // Push konstanty vidi oba stupne: vertex potrebuje matici a posun chunku,
        // fragment barvu mlhy. Vulkan chce jeden blok na cely pipeline, ne na stupen.
        var range = new PushConstantRange
        {
            StageFlags = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit,
            Offset = 0,
            Size = pushConstantSize,
        };

        var info = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 1,
            PSetLayouts = &setLayout,
            PushConstantRangeCount = 1,
            PPushConstantRanges = &range,
        };

        VulkanContext.Check(
            _context.Vk.CreatePipelineLayout(_context.Device, &info, null, out PipelineLayout layout),
            "vkCreatePipelineLayout");

        Layout = layout;
    }

    public void Dispose()
    {
        Vk vk = _context.Vk;

        if (Handle.Handle != 0)
        {
            vk.DestroyPipeline(_context.Device, Handle, null);
            Handle = default;
        }

        if (Layout.Handle != 0)
        {
            vk.DestroyPipelineLayout(_context.Device, Layout, null);
            Layout = default;
        }

        if (DescriptorSetLayout.Handle != 0)
        {
            vk.DestroyDescriptorSetLayout(_context.Device, DescriptorSetLayout, null);
            DescriptorSetLayout = default;
        }
    }
}
