using Silk.NET.Vulkan;
using StbImageSharp;

namespace Tesseris.Engine.Rendering.Vulkan;

/// <summary>
/// Textura na GPU: obraz, pohled na něj a sampler.
///
/// <para>
/// <b>Mipmapy se počítají na CPU</b>, ne přes <c>vkCmdBlitImage</c>. Důvod je praktický:
/// blit vyžaduje, aby formát uměl lineární filtrování při čtení, což se musí za běhu
/// ověřovat a ošetřovat, kdyby to zařízení neumělo. Dlaždice jsou 16×16, takže průměrování
/// čtveřic na CPU stojí zanedbatelně a výsledek je deterministický, tedy i testovatelný.
/// </para>
/// </summary>
public sealed unsafe class VulkanTexture : IDisposable
{
    private readonly VulkanContext _context;

    private Image _image;
    private DeviceMemory _memory;

    private VulkanTexture(VulkanContext context) => _context = context;

    public ImageView View { get; private set; }

    public Sampler Sampler { get; private set; }

    /// <summary>
    /// Vytvoří dvourozměrné pole textur a naplní ho. Data jsou po vrstvách, každá vrstva
    /// <paramref name="size"/>×<paramref name="size"/> v RGBA8.
    /// </summary>
    public static VulkanTexture CreateArray(
        VulkanContext context, int size, byte[][] layers, bool mipmaps, bool repeat)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(layers);

        int mipLevels = mipmaps ? (int)Math.Log2(size) + 1 : 1;
        var texture = new VulkanTexture(context);

        texture.CreateImage(
            Format.R8G8B8A8Unorm, size, size, layers.Length, mipLevels, ImageViewType.Type2DArray);
        texture.UploadLayers(size, layers, mipLevels);
        texture.CreateSampler(mipLevels, repeat, Filter.Nearest);

        return texture;
    }

    /// <summary>
    /// Vytvoří jednokanálovou dvourozměrnou texturu. Používá se na atlas fontu, kde
    /// stačí jeden bajt na texel a mipmapy by jen rozmazaly pixely.
    /// </summary>
    public static VulkanTexture CreateSingleChannel(VulkanContext context, int width, int height, byte[] pixels)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(pixels);

        var texture = new VulkanTexture(context);

        texture.CreateImage(Format.R8Unorm, width, height, 1, 1, ImageViewType.Type2D);
        texture.UploadSingle(width, height, pixels, bytesPerPixel: 1);
        texture.CreateSampler(mipLevels: 1, repeat: false, Filter.Nearest);

        return texture;
    }

    /// <summary>Creates one ordinary RGBA8 2D texture for renderer-owned dynamic content.</summary>
    public static VulkanTexture CreateRgba(
        VulkanContext context,
        int width,
        int height,
        byte[] pixels,
        bool repeat = false)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(pixels);
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        if (pixels.Length != checked(width * height * 4))
            throw new ArgumentException("RGBA data length must equal width * height * 4.", nameof(pixels));

        var texture = new VulkanTexture(context);
        texture.CreateImage(Format.R8G8B8A8Unorm, width, height, 1, 1, ImageViewType.Type2D);
        texture.UploadSingle(width, height, pixels, bytesPerPixel: 4);
        texture.CreateSampler(mipLevels: 1, repeat, Filter.Nearest);
        return texture;
    }

    /// <summary>Loads a bounded PNG into an ordinary RGBA8 2D texture.</summary>
    public static VulkanTexture CreateRgbaFromPng(
        VulkanContext context,
        string path,
        int maximumDimension = 4096)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (maximumDimension <= 0) throw new ArgumentOutOfRangeException(nameof(maximumDimension));

        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        ImageResult image = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
        if (image.Width <= 0 || image.Height <= 0
            || image.Width > maximumDimension || image.Height > maximumDimension)
            throw new InvalidDataException(
                $"Texture '{path}' dimensions {image.Width}x{image.Height} exceed {maximumDimension}x{maximumDimension}.");
        return CreateRgba(context, image.Width, image.Height, image.Data);
    }

    private void CreateImage(Format format, int width, int height, int layers, int mipLevels, ImageViewType viewType)
    {
        var imageInfo = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = format,
            Extent = new Extent3D((uint)width, (uint)height, 1),
            MipLevels = (uint)mipLevels,
            ArrayLayers = (uint)layers,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = ImageUsageFlags.TransferDstBit | ImageUsageFlags.SampledBit,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined,
        };

        VulkanContext.Check(
            _context.Vk.CreateImage(_context.Device, &imageInfo, null, out _image), "vkCreateImage");

        _context.Vk.GetImageMemoryRequirements(_context.Device, _image, out MemoryRequirements requirements);

        var allocate = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = requirements.Size,
            MemoryTypeIndex = _context.FindMemoryType(
                requirements.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit),
        };

        VulkanContext.Check(
            _context.Vk.AllocateMemory(_context.Device, &allocate, null, out _memory), "vkAllocateMemory");

        VulkanContext.Check(
            _context.Vk.BindImageMemory(_context.Device, _image, _memory, 0), "vkBindImageMemory");

        var viewInfo = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = _image,
            ViewType = viewType,
            Format = format,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = (uint)mipLevels,
                BaseArrayLayer = 0,
                LayerCount = (uint)layers,
            },
        };

        VulkanContext.Check(
            _context.Vk.CreateImageView(_context.Device, &viewInfo, null, out ImageView view), "vkCreateImageView");

        View = view;
    }

    private void UploadLayers(int size, byte[][] layers, int mipLevels)
    {
        // Vsechny vrstvy a vsechny urovne se sbali do jednoho stagingu, aby se nahravalo
        // jednim odeslanim misto desitek.
        var pieces = new List<(byte[] Data, int Level, int Layer, int Size)>();
        int total = 0;

        for (int layer = 0; layer < layers.Length; layer++)
        {
            byte[] level = layers[layer];
            int levelSize = size;

            // Podíl krycích texelů v originálu. Podle něj se každá další úroveň dorovná,
            // viz PreserveCoverage.
            float coverage = Coverage(level);

            for (int mip = 0; mip < mipLevels; mip++)
            {
                pieces.Add((level, mip, layer, levelSize));
                total += level.Length;

                if (mip + 1 < mipLevels)
                {
                    level = Downsample(level, levelSize);
                    levelSize /= 2;

                    PreserveCoverage(level, coverage);
                }
            }
        }

        using VulkanBuffer staging = VulkanBuffer.CreateHostVisible(
            _context, (ulong)total, BufferUsageFlags.TransferSrcBit);

        var regions = new BufferImageCopy[pieces.Count];
        var combined = new byte[total];
        int offset = 0;

        for (int i = 0; i < pieces.Count; i++)
        {
            (byte[] data, int level, int layer, int levelSize) = pieces[i];
            data.CopyTo(combined, offset);

            regions[i] = new BufferImageCopy
            {
                BufferOffset = (ulong)offset,
                ImageSubresource = new ImageSubresourceLayers
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    MipLevel = (uint)level,
                    BaseArrayLayer = (uint)layer,
                    LayerCount = 1,
                },
                ImageOffset = new Offset3D(0, 0, 0),
                ImageExtent = new Extent3D((uint)levelSize, (uint)levelSize, 1),
            };

            offset += data.Length;
        }

        staging.Write<byte>(combined);
        CopyToImage(staging, regions, (uint)mipLevels, (uint)layers.Length);
    }

    private void UploadSingle(int width, int height, byte[] pixels, int bytesPerPixel)
    {
        using VulkanBuffer staging = VulkanBuffer.CreateHostVisible(
            _context, (ulong)(width * height * bytesPerPixel), BufferUsageFlags.TransferSrcBit);

        staging.Write<byte>(pixels);

        var regions = new[]
        {
            new BufferImageCopy
            {
                BufferOffset = 0,
                ImageSubresource = new ImageSubresourceLayers
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    MipLevel = 0,
                    BaseArrayLayer = 0,
                    LayerCount = 1,
                },
                ImageExtent = new Extent3D((uint)width, (uint)height, 1),
            },
        };

        CopyToImage(staging, regions, mipLevels: 1, layers: 1);
    }

    private void CopyToImage(VulkanBuffer staging, BufferImageCopy[] regions, uint mipLevels, uint layers)
    {
        _context.ImmediateSubmit(commandBuffer =>
        {
            var range = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = mipLevels,
                BaseArrayLayer = 0,
                LayerCount = layers,
            };

            Transition(
                commandBuffer, range,
                ImageLayout.Undefined, ImageLayout.TransferDstOptimal,
                PipelineStageFlags2.TopOfPipeBit, 0,
                PipelineStageFlags2.AllTransferBit, AccessFlags2.TransferWriteBit);

            fixed (BufferImageCopy* regionsPtr = regions)
            {
                _context.Vk.CmdCopyBufferToImage(
                    commandBuffer, staging.Handle, _image, ImageLayout.TransferDstOptimal,
                    (uint)regions.Length, regionsPtr);
            }

            Transition(
                commandBuffer, range,
                ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal,
                PipelineStageFlags2.AllTransferBit, AccessFlags2.TransferWriteBit,
                PipelineStageFlags2.FragmentShaderBit, AccessFlags2.ShaderReadBit);
        });
    }

    private void Transition(
        CommandBuffer commandBuffer,
        ImageSubresourceRange range,
        ImageLayout from,
        ImageLayout to,
        PipelineStageFlags2 sourceStage,
        AccessFlags2 sourceAccess,
        PipelineStageFlags2 destinationStage,
        AccessFlags2 destinationAccess)
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
            Image = _image,
            SubresourceRange = range,
        };

        var dependency = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            ImageMemoryBarrierCount = 1,
            PImageMemoryBarriers = &barrier,
        };

        _context.Vk.CmdPipelineBarrier2(commandBuffer, &dependency);
    }

    /// <summary>
    /// Zmenší RGBA obraz na polovinu průměrováním čtveřic. Průměruje se v celých číslech
    /// se zaokrouhlením nahoru přičtením dvojky — bez toho by se obraz s každou úrovní
    /// nepatrně ztmavil.
    /// </summary>
    /// <remarks>
    /// <para><b>Barva se váží alfou, alfa se průměruje zvlášť.</b> Bez toho se do průměru
    /// započítávají i barvy zcela průhledných texelů — a ty jsou v dlaždicích rostlin
    /// černé, protože „díra" se kreslí jako nulová RGBA. Prostým průměrem se pak barva
    /// s každou úrovní táhne k černé, zatímco alfa klesá mnohem pomaleji.</para>
    ///
    /// <para>Ve hře to bylo vidět takhle: tráva zblízka správná, ale z dálky tmavé až
    /// černé chuchvalce, protože se z ní bere vyšší mipmapa. Nejvýrazněji na vzdáleném
    /// terénu, který dohlédne dvakrát dál než chunky, ale postihovalo to obojí.</para>
    ///
    /// <para>Váženým průměrem přispívá do barvy jen to, co je opravdu vidět.</para>
    ///
    /// <para><b>Do prázdné čtveřice se ale nesmí zapsat černá.</b> Dřív se zahazovala na nulu
    /// s odůvodněním, že ji nikdo nepřečte, když je alfa taky nula. To je nepravda a stálo
    /// to za černými skvrnami na listí v dálce a pod ostrým úhlem — přečtou ji dva:</para>
    ///
    /// <para>Trilineární přechod mezi úrovněmi (<see cref="SamplerMipmapMode"/> je Linear)
    /// mísí barvu i alfu. Kde je na jemnější úrovni průhledná černá a na hrubší krycí list,
    /// vyjde zlomková alfa a barva jen zlomek barvy listu. Alfa test v shaderech rostlin je
    /// přitom jen 0,02, takže takový fragment PROJDE — prakticky černý. Naměřeno na
    /// oak_leaves.png: mip 1 měl 698 černých texelů a 114 z nich mělo v mip 2 nad sebou
    /// krycí texel, tedy 114 míst, kde směs projde testem s barvou blízkou nule.</para>
    ///
    /// <para>Vzdálený terén čte <c>texel.rgb</c> bez alfa testu úplně (far.frag) a koruny
    /// stromů kreslí plným kvádrem s texturou listí, takže barvu děr vidí přímo. Průměr
    /// dlaždice dubového listí padal z (56,116,42) na mip 0 na (15,31,12) na mip 1 — celý
    /// les na obzoru byl tím pádem třiapůlkrát tmavší, ne jen okraje korun.</para>
    ///
    /// <para>Proto se do prázdné čtveřice zapisuje prostý průměr barev. Alfa zůstává nulová,
    /// takže se nic nezviditelní ani nikam nerozlije — pravidlo „díra nese barvu rostliny",
    /// které dosud platilo jen pro nultou úroveň (viz TextureArray.BleedColourIntoTransparent),
    /// tím projde celým řetězcem mipmap.</para>
    /// </remarks>
    internal static byte[] Downsample(byte[] source, int size)
    {
        int half = size / 2;
        byte[] result = new byte[half * half * 4];

        for (int y = 0; y < half; y++)
        {
            for (int x = 0; x < half; x++)
            {
                int destination = ((y * half) + x) * 4;

                int a = (((y * 2) * size) + (x * 2)) * 4;
                int b = (((y * 2) * size) + (x * 2) + 1) * 4;
                int c = ((((y * 2) + 1) * size) + (x * 2)) * 4;
                int d = ((((y * 2) + 1) * size) + (x * 2) + 1) * 4;

                int alphaSum = source[a + 3] + source[b + 3] + source[c + 3] + source[d + 3];

                for (int channel = 0; channel < 3; channel++)
                {
                    if (alphaSum == 0)
                    {
                        // ŽÁDNÁ ČERNÁ DO DĚR. Váhu tahle čtveřice nemá, ale barvu si nést musí -
                        // proč, stojí v poznámce nad metodou.
                        result[destination + channel] = (byte)(
                            (source[a + channel] + source[b + channel]
                                + source[c + channel] + source[d + channel] + 2) / 4);
                        continue;
                    }

                    int weighted = (source[a + channel] * source[a + 3])
                        + (source[b + channel] * source[b + 3])
                        + (source[c + channel] * source[c + 3])
                        + (source[d + channel] * source[d + 3]);

                    result[destination + channel] = (byte)((weighted + (alphaSum / 2)) / alphaSum);
                }

                result[destination + 3] = (byte)((alphaSum + 2) / 4);
            }
        }

        return result;
    }

    /// <summary>Podíl texelů, které projdou alfa testem.</summary>
    private static float Coverage(byte[] rgba)
    {
        int texels = rgba.Length / 4;
        int passing = 0;

        for (int i = 0; i < texels; i++)
        {
            if (rgba[(i * 4) + 3] >= CoverageThreshold)
            {
                passing++;
            }
        }

        return texels == 0 ? 0f : (float)passing / texels;
    }

    /// <summary>
    /// Práh, proti kterému se pokrytí měří. Musí odpovídat alfa testu v shaderech rostlin.
    /// </summary>
    private const byte CoverageThreshold = 128;

    /// <summary>
    /// Dorovná alfu zmenšené úrovně tak, aby prošel alfa testem <b>týž podíl</b> texelů
    /// jako v originále.
    /// </summary>
    /// <remarks>
    /// <para><b>Proč to nejde řešit ani průměrem, ani maximem.</b> Obojí se vyzkoušelo
    /// a obojí selhalo, každé jinak:</para>
    ///
    /// <para>Prostý <b>průměr</b> tenké tvary umazává. Stéblo široké jeden texel má ve
    /// čtveřici čtvrtinové pokrytí, takže mu alfa klesne na 64, pak na 16 a nakonec pod práh.
    /// Naměřeno: po zmenšení na osminu nezbyl u chaluhy ani jeden plně krycí texel a v dálce
    /// z rostlin byly matné fleky.</para>
    ///
    /// <para><b>Maximum</b> je opačný extrém — obsah se do prázdna ROZLÉVÁ. Prázdné řádky nad
    /// rostlinou se v každé úrovni zaplní tím, co je pod nimi, až stébla dosáhnou k horní
    /// hraně dlaždice. Ve hře z toho byl kříž na vrchu bloku, výrazný přesně u těch rostlin,
    /// které mají obsah jen dole (krátká tráva) a slabý u těch, které vyplňují dlaždici celou.
    /// Na obloze se tentýž jev projevil jako tečkovaný obrys dvou zkřížených ploch.</para>
    ///
    /// <para>Řešení je měřit <b>pokrytí</b>: kolik texelů projde alfa testem. Hledá se
    /// násobitel alfy, při kterém zmenšená úroveň pustí týž podíl jako originál — tenké tvary
    /// se tím udrží a zároveň se nikam nerozlijí. Půlením intervalu, protože podíl je vůči
    /// násobiteli nespojitý a analyticky se řešit nedá.</para>
    /// </remarks>
    private static void PreserveCoverage(byte[] rgba, float target)
    {
        // Bez průhledných míst není co dorovnávat — plné dlaždice (kámen, dřevo) se přeskočí.
        if (target <= 0f || target >= 1f)
        {
            return;
        }

        float low = 0.1f;
        float high = 8f;
        float best = 1f;

        for (int step = 0; step < 12; step++)
        {
            float scale = (low + high) * 0.5f;
            int texels = rgba.Length / 4;
            int passing = 0;

            for (int i = 0; i < texels; i++)
            {
                if (Math.Min(255f, rgba[(i * 4) + 3] * scale) >= CoverageThreshold)
                {
                    passing++;
                }
            }

            float coverage = (float)passing / texels;
            best = scale;

            if (coverage < target)
            {
                low = scale;
            }
            else
            {
                high = scale;
            }
        }

        for (int i = 0; i < rgba.Length / 4; i++)
        {
            rgba[(i * 4) + 3] = (byte)Math.Min(255f, rgba[(i * 4) + 3] * best);
        }
    }

    private void CreateSampler(int mipLevels, bool repeat, Filter magFilter)
    {
        SamplerAddressMode mode = repeat ? SamplerAddressMode.Repeat : SamplerAddressMode.ClampToEdge;

        var info = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,

            // Zvetseni bez vyhlazeni, aby dlazdice zustaly ostre. Tohle je to, co drzi
            // blokovy vzhled — zblizka musi byt videt jednotlive pixely textury.
            MagFilter = magFilter,

            // ZMENSOVANI TAKY BEZ VYHLAZENI, a je to nutnost, ne volba.
            //
            // Zkusilo se tu Linear jako lek na vlnity vzor vzdaleneho terenu. Nepomohlo
            // (pricina byla jinde — viz FarTerrain.AddSide) a PROLOMILO TO LISTI: koruny
            // se kresli michanym pruchodem a linearni filtr interpoluje ALFU mezi listem
            // a dirou, takze kolem kazdeho listu vznikl poloprusvitny lem a stromem bylo
            // videt skrz.
            //
            // Nearest drzi alfu ostrou: bud list, nebo dira. Hrany geometrie resi MSAA,
            // ktere na alfu v texture nesaha.
            MinFilter = Filter.Nearest,

            // Zmensovani pres mipmapy s linearnim prechodem mezi urovnemi, jinak
            // vzdalene plochy blikaji.
            MipmapMode = mipLevels > 1 ? SamplerMipmapMode.Linear : SamplerMipmapMode.Nearest,

            AddressModeU = mode,
            AddressModeV = mode,
            AddressModeW = mode,
            MinLod = 0f,
            MaxLod = mipLevels,
            BorderColor = BorderColor.IntOpaqueBlack,

            // ANIZOTROPNI FILTRACE JE VYPNUTA, a to zamerne.
            //
            // Zapnula se jako pokus o odstraneni vlniteho vzoru na vzdalenem terenu a vzor
            // ZHORSILA: vzorkovac voli uroven mipmapy podle MENSI osy, a to byla u sten
            // teras ta az sestnactkrat roztazena vodorovna — svisle prouzky po blocich se
            // tim vratily do ostra. Potrebny pomer by tam byl kolem 176 pri stropu 16.
            //
            // Skutecnou pricinu resi oprava UV ve FarTerrain.AddSide. Anizotropie navic
            // vyzaduje linearni MinFilter, ktery rozbiji alfu listi (viz vyse), takze by
            // se za ni platilo dvakrat.
            AnisotropyEnable = false,

            CompareEnable = false,
        };

        VulkanContext.Check(
            _context.Vk.CreateSampler(_context.Device, &info, null, out Sampler sampler), "vkCreateSampler");

        Sampler = sampler;
    }

    public void Dispose()
    {
        Vk vk = _context.Vk;

        if (Sampler.Handle != 0)
        {
            vk.DestroySampler(_context.Device, Sampler, null);
            Sampler = default;
        }

        if (View.Handle != 0)
        {
            vk.DestroyImageView(_context.Device, View, null);
            View = default;
        }

        if (_image.Handle != 0)
        {
            vk.DestroyImage(_context.Device, _image, null);
            _image = default;
        }

        if (_memory.Handle != 0)
        {
            vk.FreeMemory(_context.Device, _memory, null);
            _memory = default;
        }
    }
}
