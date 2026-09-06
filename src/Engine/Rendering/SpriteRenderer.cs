using System.Reflection;
using System.Runtime.InteropServices;
using OpenTK.Mathematics;
using Silk.NET.Vulkan;
using Tesseris.Engine.Rendering.Vulkan;

namespace Tesseris.Engine.Rendering;

/// <summary>
/// Obdélníky a ikony v souřadnicích obrazovky.
/// </summary>
/// <remarks>
/// <para>Engine uměl kreslit text a plné obdélníky, ale ne obrázek. Inventář, výroba
/// i pás pod rukou přitom stojí na tom, že je vidět, <b>co</b> hráč drží — jméno v řádku
/// dole je náhražka, ne rozhraní.</para>
///
/// <para><b>Bere se týž atlas jako pro bloky.</b> Ikona bloku je jeho vrchní stěna, takže
/// druhý atlas by znamenal tytéž obrázky podruhé a druhou sadu vazeb kvůli hrstce nástrojů.
/// Barevné obdélníky jdou stejnou cestou: záporná vrstva znamená „netexturuj", což ušetří
/// druhou pipeline i druhý buffer jen kvůli jedné barvě.</para>
///
/// <para>Stavěno podle <see cref="TextRenderer"/> včetně toho, proč je vypnutý culling
/// a proč má každý rozpracovaný snímek vlastní buffer.</para>
/// </remarks>
public sealed class SpriteRenderer : IDisposable
{
    /// <summary>Kolik obdélníků se vejde do jedné dávky.</summary>
    /// <remarks>
    /// Panel inventáře má 36 slotů, každý pozadí a ikonu, k tomu rámečky a ukazatele
    /// opotřebení. Tisíc je pohodlná rezerva a stojí 176 kB.
    /// </remarks>
    private const int MaxQuads = 1024;

    private const int VerticesPerQuad = 6;
    private const int FloatsPerVertex = 2 + 2 + 1 + 4;

    private const uint PushConstantSize = (16 * sizeof(float)) + (4 * sizeof(float));

    private readonly float[] _vertices = new float[MaxQuads * VerticesPerQuad * FloatsPerVertex];

    private readonly VulkanContext _context;
    private readonly VulkanRenderer _renderer;
    private readonly VulkanPipeline _pipeline;
    private readonly VulkanTextureSet _descriptor;
    private readonly VulkanBuffer[] _vertexBuffers;

    private int _quadCount;
    private bool _inBatch;
    private Matrix4 _projection;

    public SpriteRenderer(
        VulkanContext context, VulkanRenderer renderer, VulkanSwapchain swapchain, TextureArray atlas)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        ArgumentNullException.ThrowIfNull(swapchain);
        ArgumentNullException.ThrowIfNull(atlas);

        VertexInputAttributeDescription[] attributes =
        [
            new() { Location = 0, Binding = 0, Format = Format.R32G32Sfloat, Offset = 0 },
            new() { Location = 1, Binding = 0, Format = Format.R32G32Sfloat, Offset = 2 * sizeof(float) },
            new() { Location = 2, Binding = 0, Format = Format.R32Sfloat, Offset = 4 * sizeof(float) },
            new() { Location = 3, Binding = 0, Format = Format.R32G32B32A32Sfloat, Offset = 5 * sizeof(float) },
        ];

        _pipeline = VulkanPipeline.Create(
            context,
            swapchain,
            Assembly.GetExecutingAssembly(),
            "sprite.vert.spv",
            "sprite.frag.spv",
            vertexStride: FloatsPerVertex * sizeof(float),
            attributes,
            PushConstantSize,

            // Míchání: pozadí panelu je poloprůhledné, aby přes něj byla vidět hra.
            BlendMode.AlphaBlend,

            // Culling vypnutý ze stejného důvodu jako u textu: ortogonální projekce obrací
            // osu Y, takže se navíjení obdélníků převrátí a culling by je zahodil.
            CullModeFlags.None,

            // JEDEN VZOREK. Rozhraní se kreslí až na obrazovku, ne do vyhlazované scény —
            // s vícevzorkovým nastavením by se pipeline neshodla s cílem.
            rasterizationSamples: SampleCountFlags.Count1Bit);

        _descriptor = new VulkanTextureSet(context, _pipeline.DescriptorSetLayout, atlas.Texture);

        _vertexBuffers = new VulkanBuffer[VulkanRenderer.FramesInFlight];
        for (int i = 0; i < _vertexBuffers.Length; i++)
        {
            _vertexBuffers[i] = VulkanBuffer.CreateHostVisible(
                context, (ulong)(_vertices.Length * sizeof(float)), BufferUsageFlags.VertexBufferBit);
        }
    }

    /// <summary>
    /// Kolik obdélníků už tenhle snímek zabral. Další dávka na ně navazuje.
    /// </summary>
    /// <remarks>
    /// <para><b>Buffer je jeden na snímek, ale dávek smí být víc.</b> Druhá dávka dřív tu
    /// první přepsala, protože se psalo vždycky od začátku — a protože se kreslí až
    /// v <see cref="End"/>, zmizela ta první úplně. Přesně tak kdysi zmizel řádek s FPS
    /// u textu.</para>
    ///
    /// <para>Je to potřeba na popisek pod myší: rám musí ležet <b>nad</b> textem panelu,
    /// takže se kreslí až po něm, a to je nutně druhá dávka.</para>
    /// </remarks>
    private int _frameQuads;

    private int _lastSlot = -1;

    public void Begin(int screenWidth, int screenHeight)
    {
        Matrix4 orthographic = Matrix4.CreateOrthographicOffCenter(
            0f, screenWidth, screenHeight, 0f, -1f, 1f);

        _projection = VulkanClip.ToVulkan(orthographic);

        // Nový snímek se pozná podle toho, že se přehodil rozpracovaný snímek. Rozpočet
        // obdélníků se tím vrátí na začátek bufferu.
        int slot = _renderer.FrameSlot;

        if (slot != _lastSlot)
        {
            _lastSlot = slot;
            _frameQuads = 0;
        }

        _quadCount = 0;
        _inBatch = true;
    }

    /// <summary>Plný obdélník dané barvy.</summary>
    public void DrawRect(float x, float y, float width, float height, Vector4 color) =>
        Add(x, y, width, height, layer: -1f, color);

    /// <summary>Ikona z atlasu bloků.</summary>
    public void DrawIcon(float x, float y, float size, int layer, float brightness = 1f) =>
        Add(x, y, size, size, layer, new Vector4(brightness, brightness, brightness, 1f));

    public void DrawIconQuad(
        Vector2 a, Vector2 b, Vector2 c, Vector2 d,
        Vector2 uvA, Vector2 uvB, Vector2 uvC, Vector2 uvD,
        int layer, float brightness = 1f)
    {
        Vector4 color = new(brightness, brightness, brightness, 1f);
        AddQuad(
            (a.X, a.Y, uvA.X, uvA.Y),
            (b.X, b.Y, uvB.X, uvB.Y),
            (c.X, c.Y, uvC.X, uvC.Y),
            (d.X, d.Y, uvD.X, uvD.Y),
            layer,
            color);
    }

    /// <summary>Rámeček o zadané tloušťce. Kreslí se jako čtyři obdélníky.</summary>
    public void DrawFrame(float x, float y, float width, float height, float thickness, Vector4 color)
    {
        DrawRect(x, y, width, thickness, color);
        DrawRect(x, y + height - thickness, width, thickness, color);
        DrawRect(x, y + thickness, thickness, height - (2f * thickness), color);
        DrawRect(x + width - thickness, y + thickness, thickness, height - (2f * thickness), color);
    }

    private void AddQuad(
        (float X, float Y, float U, float V) a,
        (float X, float Y, float U, float V) b,
        (float X, float Y, float U, float V) c,
        (float X, float Y, float U, float V) d,
        float layer,
        Vector4 color)
    {
        if (!_inBatch || _frameQuads + _quadCount >= MaxQuads)
        {
            return;
        }

        int offset = _quadCount * VerticesPerQuad * FloatsPerVertex;

        // Dva trojúhelníky. Indexový buffer by pro tak malé dávky byl režie navíc.
        Span<(float X, float Y, float U, float V)> corners = [a, b, c, a, c, d];

        foreach ((float px, float py, float u, float v) in corners)
        {
            _vertices[offset++] = px;
            _vertices[offset++] = py;
            _vertices[offset++] = u;
            _vertices[offset++] = v;
            _vertices[offset++] = layer;
            _vertices[offset++] = color.X;
            _vertices[offset++] = color.Y;
            _vertices[offset++] = color.Z;
            _vertices[offset++] = color.W;
        }

        _quadCount++;
    }


    private void Add(float x, float y, float width, float height, float layer, Vector4 color)
    {
        if (!_inBatch || _frameQuads + _quadCount >= MaxQuads)
        {
            return;
        }

        int offset = _quadCount * VerticesPerQuad * FloatsPerVertex;

        // Dva trojúhelníky. Indexový buffer by pro tak malé dávky byl režie navíc.
        Span<(float X, float Y, float U, float V)> corners =
        [
            (x, y, 0f, 0f),
            (x + width, y, 1f, 0f),
            (x + width, y + height, 1f, 1f),
            (x, y, 0f, 0f),
            (x + width, y + height, 1f, 1f),
            (x, y + height, 0f, 1f),
        ];

        foreach ((float px, float py, float u, float v) in corners)
        {
            _vertices[offset++] = px;
            _vertices[offset++] = py;
            _vertices[offset++] = u;
            _vertices[offset++] = v;
            _vertices[offset++] = layer;
            _vertices[offset++] = color.X;
            _vertices[offset++] = color.Y;
            _vertices[offset++] = color.Z;
            _vertices[offset++] = color.W;
        }

        _quadCount++;
    }

    /// <summary>Odešle dávku ke kreslení.</summary>
    public unsafe void End()
    {
        _inBatch = false;

        if (_quadCount == 0)
        {
            return;
        }

        int slot = _renderer.FrameSlot;

        int firstVertex = _frameQuads * VerticesPerQuad;
        int floats = _quadCount * VerticesPerQuad * FloatsPerVertex;

        // Dávka se zapíše ZA tu předchozí a nakreslí se od svého začátku. Bez toho by
        // druhá dávka přepsala první a zůstala by po ní jen ta poslední.
        _vertexBuffers[slot].Write<float>(
            _vertices.AsSpan(0, floats), firstVertex * FloatsPerVertex * sizeof(float));

        _frameQuads += _quadCount;

        CommandBuffer commandBuffer = _renderer.CommandBuffer;
        Vk vk = _context.Vk;

        vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, _pipeline.Handle);
        _descriptor.Bind(commandBuffer, _pipeline.Layout);

        Span<float> constants = stackalloc float[20];
        MemoryMarshal.Cast<Matrix4, float>(MemoryMarshal.CreateSpan(ref _projection, 1)).CopyTo(constants);

        constants[16] = 1f;
        constants[17] = 1f;
        constants[18] = 1f;
        constants[19] = 1f;

        fixed (float* constantsPtr = constants)
        {
            vk.CmdPushConstants(
                commandBuffer,
                _pipeline.Layout,
                ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit,
                0,
                PushConstantSize,
                constantsPtr);
        }

        Silk.NET.Vulkan.Buffer buffer = _vertexBuffers[slot].Handle;
        ulong bufferOffset = 0;
        vk.CmdBindVertexBuffers(commandBuffer, 0, 1, &buffer, &bufferOffset);

        vk.CmdDraw(commandBuffer, (uint)(_quadCount * VerticesPerQuad), 1, (uint)firstVertex, 0);
    }

    public void Dispose()
    {
        foreach (VulkanBuffer buffer in _vertexBuffers)
        {
            buffer.Dispose();
        }

        _descriptor.Dispose();
        _pipeline.Dispose();
    }
}
