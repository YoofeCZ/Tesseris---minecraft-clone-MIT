using System.Reflection;
using System.Runtime.InteropServices;
using OpenTK.Mathematics;
using Silk.NET.Vulkan;
using Tesseris.Engine.MathLib;
using Tesseris.Engine.Rendering.Vulkan;

namespace Tesseris.Engine.Rendering;

/// <summary>
/// Obrys tvaru, na který hráč míří.
///
/// <para><b>Kreslí se hrany, ne stěny.</b> Zvýrazněný tvar se musí dát poznat i tehdy, když
/// vyplňuje skoro celou obrazovku — s poloprůhlednou výplní by se z toho stal barevný filtr
/// přes půl pohledu. Hrany navíc přesně ukazují, kde tvar končí, což je u dílku kmene nebo
/// u trsu trávy celý smysl věci.</para>
///
/// <para>Všechny kvádry zapsané mezi <see cref="Begin"/> a <see cref="End"/> jdou do jednoho
/// bufferu a odešlou se jediným draw callem.</para>
/// </summary>
public sealed class OutlineRenderer : IDisposable
{
    private const int FloatsPerVertex = 3;

    /// <summary>
    /// Kolik vrcholů se vejde do jedné dávky. Kvádr má dvanáct hran, tedy dvacet čtyři
    /// vrcholů; osm dílků bloku plus rezerva na otesaný blok, jehož kolizní tvar se
    /// skládá z několika sloučených kvádrů.
    /// </summary>
    private const int MaxVertices = 64 * 24;

    /// <summary>mat4 pohled-projekce + vec4 barva.</summary>
    private const uint PushConstantSize = (16 * sizeof(float)) + (4 * sizeof(float));

    private readonly float[] _vertices = new float[MaxVertices * FloatsPerVertex];

    private readonly VulkanContext _context;
    private readonly VulkanRenderer _renderer;
    private readonly VulkanPipeline _pipeline;
    private readonly VulkanBuffer[] _vertexBuffers;

    private int _vertexCount;
    private bool _inBatch;
    private Matrix4 _viewProjection;

    public OutlineRenderer(VulkanContext context, VulkanRenderer renderer, VulkanSwapchain swapchain)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        ArgumentNullException.ThrowIfNull(swapchain);

        VertexInputAttributeDescription[] attributes =
        [
            new() { Location = 0, Binding = 0, Format = Format.R32G32B32Sfloat, Offset = 0 },
        ];

        _pipeline = VulkanPipeline.Create(
            context,
            swapchain,
            Assembly.GetExecutingAssembly(),
            "outline.vert.spv",
            "outline.frag.spv",
            vertexStride: FloatsPerVertex * sizeof(float),
            attributes,
            PushConstantSize,

            // Míchá se a nezapisuje do hloubky: obrys je značka, ne těleso. Zápisem by si
            // odstínil sám sebe na protilehlých hranách.
            BlendMode.AlphaBlend,
            CullModeFlags.None,
            FrontFace.CounterClockwise,
            textureCount: 0,
            topology: PrimitiveTopology.LineList,

            // Jeden vzorek: obrys jde až na obrazovku, ne do vyhlazované scény.
            rasterizationSamples: SampleCountFlags.Count1Bit);

        _vertexBuffers = new VulkanBuffer[VulkanRenderer.FramesInFlight];
        for (int i = 0; i < _vertexBuffers.Length; i++)
        {
            _vertexBuffers[i] = VulkanBuffer.CreateHostVisible(
                context, (ulong)(_vertices.Length * sizeof(float)), BufferUsageFlags.VertexBufferBit);
        }
    }

    /// <summary>Barva obrysu i s průhledností.</summary>
    public Vector4 Color { get; set; } = new(0f, 0f, 0f, 0.55f);

    /// <summary>Zahájí dávku.</summary>
    public void Begin(Matrix4 viewProjection)
    {
        _viewProjection = viewProjection;
        _vertexCount = 0;
        _inBatch = true;
    }

    /// <summary>
    /// Přidá hrany kvádru.
    /// </summary>
    /// <param name="expand">
    /// O kolik kvádr povyrůst. Bez toho by hrany ležely přesně v rovině stěn a zápasily by
    /// s nimi o hloubku — obrys by po povrchu problikával.
    /// </param>
    public void DrawBox(Aabb box, float expand = 0.002f)
    {
        if (!_inBatch)
        {
            throw new InvalidOperationException(
                $"{nameof(DrawBox)} se musí volat mezi {nameof(Begin)} a {nameof(End)}.");
        }

        if (_vertexCount + 24 > MaxVertices)
        {
            return;
        }

        float x0 = box.Min.X - expand;
        float y0 = box.Min.Y - expand;
        float z0 = box.Min.Z - expand;
        float x1 = box.Max.X + expand;
        float y1 = box.Max.Y + expand;
        float z1 = box.Max.Z + expand;

        int offset = _vertexCount * FloatsPerVertex;

        // Spodní čtverec.
        Edge(ref offset, x0, y0, z0, x1, y0, z0);
        Edge(ref offset, x1, y0, z0, x1, y0, z1);
        Edge(ref offset, x1, y0, z1, x0, y0, z1);
        Edge(ref offset, x0, y0, z1, x0, y0, z0);

        // Horní čtverec.
        Edge(ref offset, x0, y1, z0, x1, y1, z0);
        Edge(ref offset, x1, y1, z0, x1, y1, z1);
        Edge(ref offset, x1, y1, z1, x0, y1, z1);
        Edge(ref offset, x0, y1, z1, x0, y1, z0);

        // Svislé sloupky.
        Edge(ref offset, x0, y0, z0, x0, y1, z0);
        Edge(ref offset, x1, y0, z0, x1, y1, z0);
        Edge(ref offset, x1, y0, z1, x1, y1, z1);
        Edge(ref offset, x0, y0, z1, x0, y1, z1);

        _vertexCount += 24;
    }

    /// <summary>
    /// Přidá obrys svislé plochy zadané úsečkou v půdorysu a rozsahem výšky.
    ///
    /// <para>Pro rostlinu je tohle jediný smysluplný obrys. Rostlina je dvojice ploch
    /// natažených přes úhlopříčky bloku, takže kvádr, do kterého se vejdou, je skoro
    /// celý blok — a rámeček kolem celé kostky vypadá, jako by hráč mířil na kostku,
    /// ne na kytku.</para>
    /// </summary>
    public void DrawPlane(Vector3 from, Vector3 to, float bottom, float top)
    {
        if (!_inBatch)
        {
            throw new InvalidOperationException(
                $"{nameof(DrawPlane)} se musí volat mezi {nameof(Begin)} a {nameof(End)}.");
        }

        if (_vertexCount + 8 > MaxVertices)
        {
            return;
        }

        int offset = _vertexCount * FloatsPerVertex;

        Edge(ref offset, from.X, bottom, from.Z, to.X, bottom, to.Z);
        Edge(ref offset, to.X, bottom, to.Z, to.X, top, to.Z);
        Edge(ref offset, to.X, top, to.Z, from.X, top, from.Z);
        Edge(ref offset, from.X, top, from.Z, from.X, bottom, from.Z);

        _vertexCount += 8;
    }

    /// <summary>Prida jedinou prostorovou hranu do aktualni davky.</summary>
    public void DrawLine(Vector3 from, Vector3 to)
    {
        if (!_inBatch)
        {
            throw new InvalidOperationException(
                $"{nameof(DrawLine)} se musi volat mezi {nameof(Begin)} a {nameof(End)}.");
        }

        if (_vertexCount + 2 > MaxVertices)
        {
            return;
        }

        int offset = _vertexCount * FloatsPerVertex;
        Edge(ref offset, from.X, from.Y, from.Z, to.X, to.Y, to.Z);
        _vertexCount += 2;
    }

    /// <summary>
    /// Přidá hrany kvádru otočeného kolem svislé osy. Slouží pro malé Blockbench modely
    /// ležící na zemi: jejich obrys musí následovat skutečný natočený klacík nebo kamínek,
    /// ne celý voxel ani osově zarovnanou obálku.
    /// </summary>
    public void DrawOrientedBox(Aabb box, Vector3 centre, float angle, float expand = 0.002f)
    {
        if (!_inBatch)
        {
            throw new InvalidOperationException(
                $"{nameof(DrawOrientedBox)} se musí volat mezi {nameof(Begin)} a {nameof(End)}.");
        }

        if (_vertexCount + 24 > MaxVertices)
        {
            return;
        }

        float x0 = box.Min.X - expand;
        float y0 = box.Min.Y - expand;
        float z0 = box.Min.Z - expand;
        float x1 = box.Max.X + expand;
        float y1 = box.Max.Y + expand;
        float z1 = box.Max.Z + expand;
        float cosine = MathF.Cos(angle);
        float sine = MathF.Sin(angle);

        Span<Vector3> corners = stackalloc Vector3[8]
        {
            Rotate(new Vector3(x0, y0, z0)), Rotate(new Vector3(x1, y0, z0)),
            Rotate(new Vector3(x0, y1, z0)), Rotate(new Vector3(x1, y1, z0)),
            Rotate(new Vector3(x0, y0, z1)), Rotate(new Vector3(x1, y0, z1)),
            Rotate(new Vector3(x0, y1, z1)), Rotate(new Vector3(x1, y1, z1)),
        };

        int offset = _vertexCount * FloatsPerVertex;
        Edge(ref offset, corners[0].X, corners[0].Y, corners[0].Z, corners[1].X, corners[1].Y, corners[1].Z);
        Edge(ref offset, corners[1].X, corners[1].Y, corners[1].Z, corners[5].X, corners[5].Y, corners[5].Z);
        Edge(ref offset, corners[5].X, corners[5].Y, corners[5].Z, corners[4].X, corners[4].Y, corners[4].Z);
        Edge(ref offset, corners[4].X, corners[4].Y, corners[4].Z, corners[0].X, corners[0].Y, corners[0].Z);
        Edge(ref offset, corners[2].X, corners[2].Y, corners[2].Z, corners[3].X, corners[3].Y, corners[3].Z);
        Edge(ref offset, corners[3].X, corners[3].Y, corners[3].Z, corners[7].X, corners[7].Y, corners[7].Z);
        Edge(ref offset, corners[7].X, corners[7].Y, corners[7].Z, corners[6].X, corners[6].Y, corners[6].Z);
        Edge(ref offset, corners[6].X, corners[6].Y, corners[6].Z, corners[2].X, corners[2].Y, corners[2].Z);
        Edge(ref offset, corners[0].X, corners[0].Y, corners[0].Z, corners[2].X, corners[2].Y, corners[2].Z);
        Edge(ref offset, corners[1].X, corners[1].Y, corners[1].Z, corners[3].X, corners[3].Y, corners[3].Z);
        Edge(ref offset, corners[5].X, corners[5].Y, corners[5].Z, corners[7].X, corners[7].Y, corners[7].Z);
        Edge(ref offset, corners[4].X, corners[4].Y, corners[4].Z, corners[6].X, corners[6].Y, corners[6].Z);
        _vertexCount += 24;

        Vector3 Rotate(Vector3 point)
        {
            float x = point.X - centre.X;
            float z = point.Z - centre.Z;
            return new Vector3(
                centre.X + (x * cosine) + (z * sine),
                point.Y,
                centre.Z + (z * cosine) - (x * sine));
        }

    }

    /// <summary>Odešle celou dávku jedním draw callem.</summary>
    public unsafe void End(RenderStats stats)
    {
        ArgumentNullException.ThrowIfNull(stats);

        _inBatch = false;

        if (_vertexCount == 0)
        {
            return;
        }

        int vertexCount = _vertexCount;
        int floatCount = vertexCount * FloatsPerVertex;

        VulkanBuffer buffer = _vertexBuffers[_renderer.FrameSlot];
        buffer.Write<float>(_vertices.AsSpan(0, floatCount));

        CommandBuffer commandBuffer = _renderer.CommandBuffer;
        Vk vk = _context.Vk;

        vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, _pipeline.Handle);

        Span<float> push = stackalloc float[20];
        MemoryMarshal.Cast<Matrix4, float>(MemoryMarshal.CreateSpan(ref _viewProjection, 1)).CopyTo(push);
        push[16] = Color.X;
        push[17] = Color.Y;
        push[18] = Color.Z;
        push[19] = Color.W;

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

        vk.CmdDraw(commandBuffer, (uint)vertexCount, 1, 0, 0);
        stats.CountDrawCall();
    }

    public void Dispose()
    {
        foreach (VulkanBuffer buffer in _vertexBuffers)
        {
            buffer.Dispose();
        }

        _pipeline.Dispose();
    }

    private void Edge(ref int offset, float ax, float ay, float az, float bx, float by, float bz)
    {
        _vertices[offset++] = ax;
        _vertices[offset++] = ay;
        _vertices[offset++] = az;

        _vertices[offset++] = bx;
        _vertices[offset++] = by;
        _vertices[offset++] = bz;
    }
}
