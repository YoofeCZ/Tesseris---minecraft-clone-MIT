using System.Reflection;
using System.Runtime.InteropServices;
using OpenTK.Mathematics;
using Silk.NET.Vulkan;
using Tesseris.Engine.Rendering.Vulkan;

namespace Tesseris.Engine.Rendering;

/// <summary>
/// Vykreslování textu bitmapovým fontem <see cref="Font8x8"/>.
///
/// Font se za běhu rozbalí do jednokanálové textury 128×48 (mřížka 16×6 buněk 8×8).
/// Všechny řetězce zapsané mezi <see cref="Begin"/> a <see cref="End"/> se sázejí do jednoho
/// bufferu a odešlou jediným draw callem — počet draw callů v overlay tak nezávisí na tom,
/// kolik řádků se zrovna vypisuje.
///
/// Souřadnice jsou v pixelech obrazovky, počátek vlevo nahoře, osa Y míří dolů.
/// </summary>
public sealed class TextRenderer : IDisposable
{
    private const int AtlasColumns = 16;
    private const int AtlasRows = 6; // 95 glyfů se vejde do 96 buněk
    private const int AtlasWidth = AtlasColumns * Font8x8.GlyphWidth;   // 128
    private const int AtlasHeight = AtlasRows * Font8x8.GlyphHeight;    // 48

    /// <summary>Posun na další znak v buňkách fontu. Kresba je 5 px široká, 6 dá mezeru 1 px.</summary>
    public const int AdvanceX = Font8x8.InkWidth + 1;

    /// <summary>
    /// Rozteč řádků. Musí být větší než výška buňky, protože řádek 7 nese dolní dotahy
    /// (g j p q y , ; _) — při rozteči přesně 8 by se dotah dotkl verzálky dalšího řádku.
    /// </summary>
    public const int LineHeight = Font8x8.GlyphHeight + 2;

    private const int FloatsPerVertex = 4;   // vec2 pozice + vec2 uv
    private const int VerticesPerGlyph = 6;  // dva trojúhelníky
    // Zvednuto z 512 kvuli vyvojarskemu menu: to ma pres 20 radku a samo o sobe
    // prekrocilo puvodni strop, takze se tise orizlo uprostred.
    private const int MaxGlyphs = 4096;

    /// <summary>
    /// Buňka atlasu vyplněná celá.
    ///
    /// <para>Font má 95 glyfů a mřížka 16×6 jich pojme 96, takže poslední buňka zbývá
    /// volná. Vyplní se a slouží ke kreslení plných obdélníků — zaměřovače a podobných
    /// čar. Díky tomu nepotřebují vlastní pipeline ani texturu a jdou do téže dávky
    /// jako text, tedy jedním draw callem navíc nula.</para>
    /// </summary>
    private const int SolidCellIndex = (AtlasColumns * AtlasRows) - 1;

    /// <summary>mat4 projekce + vec4 barva. Nahrazuje uniformy z OpenGL verze.</summary>
    private const uint PushConstantSize = (16 * sizeof(float)) + (4 * sizeof(float));

    private readonly float[] _vertices = new float[MaxGlyphs * VerticesPerGlyph * FloatsPerVertex];

    private readonly VulkanContext _context;
    private readonly VulkanRenderer _renderer;
    private readonly VulkanPipeline _pipeline;
    private readonly VulkanTexture _atlas;
    private readonly VulkanTextureSet _descriptor;

    // Jeden buffer na každý rozpracovaný snímek. Se sdíleným bufferem by se přepisovala
    // data, ze kterých GPU ještě kreslí předchozí snímek.
    private readonly VulkanBuffer[] _vertexBuffers;

    private int _glyphCount;
    private bool _inBatch;
    private Matrix4 _projection;

    /// <summary>Kolik glyfů už tenhle snímek zabral. Další dávka na ně navazuje.</summary>
    private int _frameGlyphs;

    private int _lastSlot = -1;

    public TextRenderer(VulkanContext context, VulkanRenderer renderer, VulkanSwapchain swapchain)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        ArgumentNullException.ThrowIfNull(swapchain);

        VertexInputAttributeDescription[] attributes =
        [
            new() { Location = 0, Binding = 0, Format = Format.R32G32Sfloat, Offset = 0 },
            new() { Location = 1, Binding = 0, Format = Format.R32G32Sfloat, Offset = 2 * sizeof(float) },
        ];

        _pipeline = VulkanPipeline.Create(
            context,
            swapchain,
            Assembly.GetExecutingAssembly(),
            "text.vert.spv",
            "text.frag.spv",
            vertexStride: FloatsPerVertex * sizeof(float),
            attributes,
            PushConstantSize,
            BlendMode.Overlay,

            // Culling se musí vypnout: ortogonální projekce obrací osu Y, takže se navíjení
            // textových obdélníků převrátí a zapnutý culling by text beze stopy zahodil.
            // Stejný důvod jako v OpenGL verzi, jen se to nastavuje v pipeline, ne za běhu.
            CullModeFlags.None,

            // Jeden vzorek: text jde až na obrazovku, viz SpriteRenderer.
            rasterizationSamples: SampleCountFlags.Count1Bit);

        _atlas = VulkanTexture.CreateSingleChannel(context, AtlasWidth, AtlasHeight, BuildAtlas());
        _descriptor = new VulkanTextureSet(context, _pipeline.DescriptorSetLayout, _atlas);

        _vertexBuffers = new VulkanBuffer[VulkanRenderer.FramesInFlight];
        for (int i = 0; i < _vertexBuffers.Length; i++)
        {
            _vertexBuffers[i] = VulkanBuffer.CreateHostVisible(
                context, (ulong)(_vertices.Length * sizeof(float)), BufferUsageFlags.VertexBufferBit);
        }
    }

    /// <summary>Barva textu. Font je jednobitový, takže barva je uniformní pro celou dávku.</summary>
    public Vector3 Color { get; set; } = new(1f, 1f, 1f);

    /// <summary>Zahájí dávku. Rozměry se předávají v pixelech okna.</summary>
    public void Begin(int screenWidth, int screenHeight)
    {
        // Počátek vlevo nahoře, Y dolů — pro sazbu textu čitelnější než GL konvence zdola.
        Matrix4 orthographic = Matrix4.CreateOrthographicOffCenter(
            0f, screenWidth, screenHeight, 0f, -1f, 1f);

        // Ortogonální projekce pro počátek vlevo nahoře už osu Y jednou obrací; vulkanská
        // korekce ji obrátí podruhé, čímž se trefí do toho, že ve Vulkanu je nahoře −1.
        _projection = VulkanClip.ToVulkan(orthographic);

        // Nový snímek se pozná podle přehozeného rozpracovaného snímku; tím se rozpočet
        // glyfů vrátí na začátek bufferu.
        int slot = _renderer.FrameSlot;

        if (slot != _lastSlot)
        {
            _lastSlot = slot;
            _frameGlyphs = 0;
        }

        _glyphCount = 0;
        _inBatch = true;
    }

    /// <summary>Zapíše řetězec do dávky. Znaky mimo ASCII 32..126 se přeskočí.</summary>
    /// <param name="scale">Celočíselné zvětšení, aby pixely fontu zůstaly ostré.</param>
    /// <summary>
    /// Nahradí písmeno s diakritikou jeho základem.
    /// </summary>
    /// <remarks>
    /// <para><b>Font je ASCII osm na osm.</b> Česká písmena v něm nejsou, takže se dosud
    /// kreslila jako mezera — „přilba" vyšla jako „p ilba" a v receptáři to vypadalo jako
    /// chyba v datech. Přitom data správná jsou; chybí jen tvary.</para>
    ///
    /// <para><b>Skládá se to při kreslení, ne v datech.</b> Jména předmětů zůstávají česky
    /// se vším všudy — až font diakritiku dostane, začne se kreslit sama a nikde se nemusí
    /// nic přepisovat zpátky.</para>
    /// </remarks>
    private static char Fold(char c) => c switch
    {
        'á' or 'à' or 'â' or 'ä' => 'a',
        'č' => 'c',
        'ď' => 'd',
        'é' or 'ě' or 'è' or 'ê' => 'e',
        'í' or 'ì' or 'î' => 'i',
        'ň' => 'n',
        'ó' or 'ò' or 'ô' or 'ö' => 'o',
        'ř' => 'r',
        'š' => 's',
        'ť' => 't',
        'ú' or 'ů' or 'ù' or 'û' or 'ü' => 'u',
        'ý' => 'y',
        'ž' => 'z',
        'Á' or 'À' or 'Â' or 'Ä' => 'A',
        'Č' => 'C',
        'Ď' => 'D',
        'É' or 'Ě' or 'È' or 'Ê' => 'E',
        'Í' or 'Ì' or 'Î' => 'I',
        'Ň' => 'N',
        'Ó' or 'Ò' or 'Ô' or 'Ö' => 'O',
        'Ř' => 'R',
        'Š' => 'S',
        'Ť' => 'T',
        'Ú' or 'Ů' or 'Ù' or 'Û' or 'Ü' => 'U',
        'Ý' => 'Y',
        'Ž' => 'Z',
        _ => c,
    };

    public void DrawText(string text, int x, int y, int scale = 2)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (!_inBatch)
        {
            throw new InvalidOperationException($"{nameof(DrawText)} se musí volat mezi {nameof(Begin)} a {nameof(End)}.");
        }

        int penX = x;

        foreach (char raw in text)
        {
            char c = Fold(raw);

            if (c == '\n')
            {
                penX = x;
                y += LineHeight * scale;
                continue;
            }

            if (c < Font8x8.FirstChar || c > Font8x8.LastChar)
            {
                penX += AdvanceX * scale;
                continue;
            }

            // Mezera nemá co kreslit; přeskočením se ušetří šestina bufferu na běžném textu.
            if (c != ' ')
            {
                if (_frameGlyphs + _glyphCount >= MaxGlyphs)
                {
                    break;
                }

                AppendGlyph(c, penX, y, scale);
                _glyphCount++;
            }

            penX += AdvanceX * scale;
        }
    }

    /// <summary>
    /// Vykreslí plný obdélník. Souřadnice a rozměry jsou v pixelech obrazovky.
    ///
    /// <para>Barvu bere z <see cref="Color"/> stejně jako text — celá dávka má jednu.</para>
    /// </summary>
    public void DrawRect(int x, int y, int width, int height)
    {
        if (!_inBatch)
        {
            throw new InvalidOperationException(
                $"{nameof(DrawRect)} se musí volat mezi {nameof(Begin)} a {nameof(End)}.");
        }

        if (width <= 0 || height <= 0 || _frameGlyphs + _glyphCount >= MaxGlyphs)
        {
            return;
        }

        int col = SolidCellIndex % AtlasColumns;
        int row = SolidCellIndex / AtlasColumns;

        // Půl texelu dovnitř, ať filtrování nesáhne na sousední buňku atlasu a obdélník
        // nezůstane u kraje průhledný.
        float u0 = ((col * Font8x8.GlyphWidth) + 0.5f) / AtlasWidth;
        float v0 = ((row * Font8x8.GlyphHeight) + 0.5f) / AtlasHeight;

        int offset = _glyphCount * VerticesPerGlyph * FloatsPerVertex;

        float x0 = x;
        float y0 = y;
        float x1 = x + width;
        float y1 = y + height;

        Write(ref offset, x0, y0, u0, v0);
        Write(ref offset, x1, y0, u0, v0);
        Write(ref offset, x1, y1, u0, v0);

        Write(ref offset, x0, y0, u0, v0);
        Write(ref offset, x1, y1, u0, v0);
        Write(ref offset, x0, y1, u0, v0);

        _glyphCount++;
    }

    /// <summary>Odešle celou dávku jedním draw callem.</summary>
    public unsafe void End(RenderStats stats)
    {
        ArgumentNullException.ThrowIfNull(stats);

        _inBatch = false;
        if (_glyphCount == 0)
        {
            return;
        }

        int vertexCount = _glyphCount * VerticesPerGlyph;
        int floatCount = vertexCount * FloatsPerVertex;

        // DÁVKA SE ZAPÍŠE ZA TU PŘEDCHOZÍ, ne přes ni.
        //
        // Buffer je jeden na snímek. Dokud se psalo vždycky od začátku, přepsala druhá
        // dávka tu první dřív, než ji grafika stihla nakreslit — takhle kdysi zmizel
        // řádek s FPS, jakmile začal inventář kreslit vlastní text. Popisek pod myší musí
        // ležet nad vším ostatním, takže druhou dávku potřebuje.
        int firstVertex = _frameGlyphs * VerticesPerGlyph;

        VulkanBuffer buffer = _vertexBuffers[_renderer.FrameSlot];
        buffer.Write<float>(_vertices.AsSpan(0, floatCount), firstVertex * FloatsPerVertex * sizeof(float));

        _frameGlyphs += _glyphCount;

        CommandBuffer commandBuffer = _renderer.CommandBuffer;
        Vk vk = _context.Vk;

        vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, _pipeline.Handle);
        _descriptor.Bind(commandBuffer, _pipeline.Layout);

        Span<float> push = stackalloc float[20];
        MemoryMarshal.Cast<Matrix4, float>(MemoryMarshal.CreateSpan(ref _projection, 1)).CopyTo(push);
        push[16] = Color.X;
        push[17] = Color.Y;
        push[18] = Color.Z;
        push[19] = 1f;

        fixed (float* pushPtr = push)
        {
            vk.CmdPushConstants(
                commandBuffer,
                _pipeline.Layout,
                ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit,
                0,
                PushConstantSize,
                pushPtr);
        }

        Silk.NET.Vulkan.Buffer vertexBuffer = buffer.Handle;
        ulong offset = 0;
        vk.CmdBindVertexBuffers(commandBuffer, 0, 1, &vertexBuffer, &offset);

        vk.CmdDraw(commandBuffer, (uint)vertexCount, 1, (uint)firstVertex, 0);
        stats.CountDrawCall();
    }

    public void Dispose()
    {
        foreach (VulkanBuffer buffer in _vertexBuffers)
        {
            buffer.Dispose();
        }

        _descriptor.Dispose();
        _atlas.Dispose();
        _pipeline.Dispose();
    }

    private void AppendGlyph(char c, int x, int y, int scale)
    {
        int index = c - Font8x8.FirstChar;
        int col = index % AtlasColumns;
        int row = index / AtlasColumns;

        float u0 = col * Font8x8.GlyphWidth / (float)AtlasWidth;
        float v0 = row * Font8x8.GlyphHeight / (float)AtlasHeight;
        float u1 = ((col * Font8x8.GlyphWidth) + Font8x8.GlyphWidth) / (float)AtlasWidth;
        float v1 = ((row * Font8x8.GlyphHeight) + Font8x8.GlyphHeight) / (float)AtlasHeight;

        float x0 = x;
        float y0 = y;
        float x1 = x + (Font8x8.GlyphWidth * scale);
        float y1 = y + (Font8x8.GlyphHeight * scale);

        int offset = _glyphCount * VerticesPerGlyph * FloatsPerVertex;

        Write(ref offset, x0, y0, u0, v0);
        Write(ref offset, x1, y0, u1, v0);
        Write(ref offset, x1, y1, u1, v1);

        Write(ref offset, x0, y0, u0, v0);
        Write(ref offset, x1, y1, u1, v1);
        Write(ref offset, x0, y1, u0, v1);
    }

    private void Write(ref int offset, float x, float y, float u, float v)
    {
        _vertices[offset++] = x;
        _vertices[offset++] = y;
        _vertices[offset++] = u;
        _vertices[offset++] = v;
    }

    private static byte[] BuildAtlas()
    {
        byte[] pixels = new byte[AtlasWidth * AtlasHeight];

        for (int index = 0; index < Font8x8.CharCount; index++)
        {
            int col = index % AtlasColumns;
            int row = index / AtlasColumns;
            char c = (char)(Font8x8.FirstChar + index);

            for (int y = 0; y < Font8x8.GlyphHeight; y++)
            {
                int destRow = ((row * Font8x8.GlyphHeight) + y) * AtlasWidth;
                for (int x = 0; x < Font8x8.GlyphWidth; x++)
                {
                    if (Font8x8.Pixel(c, x, y))
                    {
                        pixels[destRow + (col * Font8x8.GlyphWidth) + x] = 255;
                    }
                }
            }
        }

        // Volná buňka na konci atlasu se vyplní celá — kreslí se z ní plné obdélníky.
        int solidCol = SolidCellIndex % AtlasColumns;
        int solidRow = SolidCellIndex / AtlasColumns;

        for (int y = 0; y < Font8x8.GlyphHeight; y++)
        {
            int destRow = ((solidRow * Font8x8.GlyphHeight) + y) * AtlasWidth;

            for (int x = 0; x < Font8x8.GlyphWidth; x++)
            {
                pixels[destRow + (solidCol * Font8x8.GlyphWidth) + x] = 255;
            }
        }

        return pixels;
    }
}
