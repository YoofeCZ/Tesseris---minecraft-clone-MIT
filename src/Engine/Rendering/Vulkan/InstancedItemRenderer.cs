using System.Reflection;
using System.Runtime.InteropServices;
using OpenTK.Mathematics;
using Silk.NET.Vulkan;
using Tesseris.Engine.MathLib;

namespace Tesseris.Engine.Rendering.Vulkan;

/// <summary>Co odlišuje jeden item od druhého. Tohle jde do per-instance bufferu.</summary>
/// <remarks>
/// Šestnáct bajtů na item. Při 20 000 itemech je to 320 kB na snímek proti 15,4 MB, které
/// stála původní cesta se skládáním geometrie na CPU.
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct ItemInstance
{
    /// <summary>Střed itemu ve světě.</summary>
    public Vector3 Offset;

    /// <summary>Vrstva v texturovém poli.</summary>
    public float Layer;

    /// <summary>Velikost kostky v blocích.</summary>
    public float Scale;

    /// <summary>Kolik na item svítí, 0 až 1.</summary>
    public float Light;

    /// <summary>Zarovnání na 24 B. Bez něj by se stride rozešel s tím, co čeká shader.</summary>
    private readonly float _padA;
    private readonly float _padB;
}

/// <summary>
/// Kreslí itemy instancovaně: jedna geometrie, tisíce poloh, jeden draw call.
/// </summary>
/// <remarks>
/// <para><b>Proč to vzniklo.</b> Audit zjistil, že v projektu neexistoval jediný instancovaný
/// draw call — všech deset volání kreslení mělo <c>instanceCount = 1</c>. Ležící předměty se
/// každý snímek znovu skládaly na CPU do jedné velké mesh a celá se nahrávala na grafiku.
/// Jeden item stál 24 vrcholů po 32 bajtech, tedy 768 B geometrie na snímek; při 20 000
/// itemech přes 15 MB na snímek, což je při 60 snímcích skoro gigabajt za vteřinu.</para>
///
/// <para><b>Co se změnilo.</b> Kostka se nahraje jednou při startu a už se jí nikdo nedotkne.
/// Na snímek se posílá jen 24 bajtů na item — poloha, vrstva textury, velikost a světlo.
/// To je šedesátkrát míň dat a jeden draw call místo skládání.</para>
///
/// <para><b>Cullování je součást, ne přídavek.</b> Pravidlo 6.7 říká „renderujeme jen to, co
/// je v záběru". Instance se plní přes <see cref="Add"/>, které zahodí, co je mimo frustum —
/// itemy za zády se dál simulují, ale nemají žádnou vizuální reprezentaci.</para>
/// </remarks>
public sealed unsafe class InstancedItemRenderer : IDisposable
{
    /// <summary>Kolik bajtů zabírá jedna instance. Musí sedět na vazbu 1 v shaderu.</summary>
    public static readonly uint InstanceStride = (uint)sizeof(ItemInstance);

    private const int FloatsPerVertex = 5;
    private const uint VertexStride = FloatsPerVertex * sizeof(float);

    private readonly VulkanContext _context;
    private readonly VulkanRenderer _renderer;
    private readonly VulkanPipeline _pipeline;
    private readonly VulkanBuffer _cubeVertices;
    private readonly VulkanBuffer _cubeIndices;
    private readonly VulkanBuffer[] _instances;
    private readonly int _maximumInstances;

    private ItemInstance[] _staging;
    private int _count;

    public InstancedItemRenderer(
        VulkanContext context,
        VulkanRenderer renderer,
        VulkanSwapchain swapchain,
        Assembly shaderAssembly,
        uint pushConstantSize,
        int maximumInstances = 32_768,
        Format? colorFormat = null,
        SampleCountFlags? rasterizationSamples = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(swapchain);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumInstances, 1);

        _context = context;
        _renderer = renderer;
        _maximumInstances = maximumInstances;
        _staging = new ItemInstance[maximumInstances];

        // Vazba 0 je geometrie kostky, vazba 1 data instance. Locations 2 az 5 tedy
        // ctou z druheho bufferu a posouvaji se s instanci, ne s vrcholem.
        VertexInputAttributeDescription[] attributes =
        [
            new() { Location = 0, Binding = 0, Format = Format.R32G32B32Sfloat, Offset = 0 },
            new() { Location = 1, Binding = 0, Format = Format.R32G32Sfloat, Offset = 3 * sizeof(float) },
            new() { Location = 2, Binding = 1, Format = Format.R32G32B32Sfloat, Offset = 0 },
            new() { Location = 3, Binding = 1, Format = Format.R32Sfloat, Offset = 3 * sizeof(float) },
            new() { Location = 4, Binding = 1, Format = Format.R32Sfloat, Offset = 4 * sizeof(float) },
            new() { Location = 5, Binding = 1, Format = Format.R32Sfloat, Offset = 5 * sizeof(float) },
        ];

        _pipeline = VulkanPipeline.Create(
            context,
            swapchain,
            shaderAssembly,
            "item_instanced.vert.spv",
            "item_instanced.frag.spv",
            VertexStride,
            attributes,
            pushConstantSize,
            BlendMode.Cutout,
            CullModeFlags.BackBit,
            colorFormat: colorFormat,
            rasterizationSamples: rasterizationSamples,
            instanceStride: InstanceStride);

        (float[] vertices, uint[] indices) = BuildCube();

        _cubeVertices = VulkanBuffer.CreateHostVisible(
            context, (ulong)(vertices.Length * sizeof(float)), BufferUsageFlags.VertexBufferBit);
        _cubeVertices.Write<float>(vertices);

        _cubeIndices = VulkanBuffer.CreateHostVisible(
            context, (ulong)(indices.Length * sizeof(uint)), BufferUsageFlags.IndexBufferBit);
        _cubeIndices.Write<uint>(indices);

        IndexCount = (uint)indices.Length;

        // Jeden buffer na snímek v letu: přepisovat ten, ze kterého grafika ještě čte,
        // je přesně ta chyba, na kterou ovladač nadává.
        _instances = new VulkanBuffer[VulkanRenderer.FramesInFlight];
        for (int i = 0; i < _instances.Length; i++)
        {
            _instances[i] = VulkanBuffer.CreateHostVisible(
                context,
                (ulong)maximumInstances * InstanceStride,
                BufferUsageFlags.VertexBufferBit);
        }
    }

    /// <summary>Kolik indexů má sdílená kostka.</summary>
    public uint IndexCount { get; }

    /// <summary>
    /// Layout deskriptorů téhle pipeline.
    /// </summary>
    /// <remarks>
    /// Sada se MUSÍ založit z tohohle layoutu, ne z cizího. Sdílet descriptor set
    /// s neprůhledným průchodem nejde — ten má pět vazeb (stínové mapy), tahle pipeline
    /// jednu, a validační vrstva to hlásí jako nekompatibilní layout.
    /// </remarks>
    public DescriptorSetLayout DescriptorSetLayout => _pipeline.DescriptorSetLayout;

    /// <summary>Kolik instancí se nasbíralo pro tenhle snímek.</summary>
    public int Count => _count;

    /// <summary>Kolik instancí se zahodilo, protože nebyly v záběru.</summary>
    public int CulledCount { get; private set; }

    /// <summary>Kolik instancí se zahodilo, protože se nevešly do bufferu.</summary>
    public int OverflowCount { get; private set; }

    /// <summary>Kolik milisekund zabralo poslední nahrání instancí na grafiku.</summary>
    public double LastUploadMs { get; private set; }

    /// <summary>Začne sbírat instance pro nový snímek.</summary>
    public void BeginFrame()
    {
        _count = 0;
        CulledCount = 0;
        OverflowCount = 0;
        LastUploadMs = 0.0;
    }

    /// <summary>
    /// Přidá item ke kreslení, pokud je v záběru.
    /// </summary>
    /// <returns>false, když byl zahozen (mimo záběr nebo plný buffer).</returns>
    public bool Add(Frustum frustum, Vector3 centre, float scale, float layer, float light)
    {
        // Obal kolem kostky. Frustum umí kvádr, ne kouli, takže se testuje AABB.
        float half = scale * 0.5f;
        var bounds = new Aabb(
            new Vector3(centre.X - half, centre.Y - half, centre.Z - half),
            new Vector3(centre.X + half, centre.Y + half, centre.Z + half));

        if (!frustum.Intersects(bounds))
        {
            CulledCount++;
            return false;
        }

        if (_count >= _maximumInstances)
        {
            OverflowCount++;
            return false;
        }

        _staging[_count++] = new ItemInstance
        {
            Offset = centre,
            Layer = layer,
            Scale = scale,
            Light = light,
        };

        return true;
    }

    /// <summary>Přidá item bez kontroly záběru. Pro měření a pro testy.</summary>
    public bool AddUnculled(Vector3 centre, float scale, float layer, float light)
    {
        if (_count >= _maximumInstances)
        {
            OverflowCount++;
            return false;
        }

        _staging[_count++] = new ItemInstance
        {
            Offset = centre,
            Layer = layer,
            Scale = scale,
            Light = light,
        };

        return true;
    }

    /// <summary>
    /// Nahraje nasbírané instance a vykreslí je <b>jedním</b> draw callem.
    /// </summary>
    /// <returns>Kolik draw callů se vydalo. Nula nebo jedna.</returns>
    public int Draw(CommandBuffer commandBuffer, VulkanTextureSet textures, ReadOnlySpan<float> pushConstants)
    {
        ArgumentNullException.ThrowIfNull(textures);

        if (_count == 0)
        {
            return 0;
        }

        Vk vk = _context.Vk;
        int frame = _renderer.FrameSlot % _instances.Length;
        VulkanBuffer instanceBuffer = _instances[frame];

        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        instanceBuffer.Write<ItemInstance>(_staging.AsSpan(0, _count));
        LastUploadMs = (System.Diagnostics.Stopwatch.GetTimestamp() - start)
            * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

        vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, _pipeline.Handle);
        textures.Bind(commandBuffer, _pipeline.Layout);

        fixed (float* constants = pushConstants)
        {
            vk.CmdPushConstants(
                commandBuffer,
                _pipeline.Layout,
                ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit,
                0,
                (uint)pushConstants.Length * sizeof(float),
                constants);
        }

        // DVĚ VAZBY NARÁZ: nula geometrie, jedna instance.
        Silk.NET.Vulkan.Buffer* buffers = stackalloc Silk.NET.Vulkan.Buffer[2];
        buffers[0] = _cubeVertices.Handle;
        buffers[1] = instanceBuffer.Handle;

        ulong* offsets = stackalloc ulong[2];
        offsets[0] = 0;
        offsets[1] = 0;

        vk.CmdBindVertexBuffers(commandBuffer, 0, 2, buffers, offsets);
        vk.CmdBindIndexBuffer(commandBuffer, _cubeIndices.Handle, 0, IndexType.Uint32);

        // TADY JE CELÝ ROZDÍL: instanceCount není jedna.
        vk.CmdDrawIndexed(commandBuffer, IndexCount, (uint)_count, 0, 0, 0);
        return 1;
    }

    /// <summary>
    /// Kostka o hraně jedna se středem v počátku.
    /// </summary>
    /// <remarks>
    /// Šest stěn po čtyřech vrcholech, ne osm sdílených — každá stěna má vlastní UV, takže
    /// se vrcholy sdílet nedají. Navíjení proti směru hodinových ručiček, stejně jako zbytek
    /// projektu (viz poznámka u <see cref="VulkanPipeline.Create"/>).
    /// </remarks>
    private static (float[] Vertices, uint[] Indices) BuildCube()
    {
        var vertices = new List<float>(24 * FloatsPerVertex);
        var indices = new List<uint>(36);

        ReadOnlySpan<Vector3> normals =
        [
            new(0, 0, 1), new(0, 0, -1),
            new(1, 0, 0), new(-1, 0, 0),
            new(0, 1, 0), new(0, -1, 0),
        ];

        foreach (Vector3 normal in normals)
        {
            // Dvě osy kolmé na normálu.
            Vector3 up = MathF.Abs(normal.Y) > 0.5f ? new Vector3(0, 0, 1) : new Vector3(0, 1, 0);
            Vector3 right = Vector3.Normalize(Vector3.Cross(up, normal));
            up = Vector3.Normalize(Vector3.Cross(normal, right));

            Vector3 centre = normal * 0.5f;
            uint baseIndex = (uint)(vertices.Count / FloatsPerVertex);

            AddVertex(vertices, centre - (right * 0.5f) - (up * 0.5f), 0f, 1f);
            AddVertex(vertices, centre + (right * 0.5f) - (up * 0.5f), 1f, 1f);
            AddVertex(vertices, centre + (right * 0.5f) + (up * 0.5f), 1f, 0f);
            AddVertex(vertices, centre - (right * 0.5f) + (up * 0.5f), 0f, 0f);

            indices.Add(baseIndex);
            indices.Add(baseIndex + 1);
            indices.Add(baseIndex + 2);
            indices.Add(baseIndex);
            indices.Add(baseIndex + 2);
            indices.Add(baseIndex + 3);
        }

        return (vertices.ToArray(), indices.ToArray());
    }

    private static void AddVertex(List<float> target, Vector3 position, float u, float v)
    {
        target.Add(position.X);
        target.Add(position.Y);
        target.Add(position.Z);
        target.Add(u);
        target.Add(v);
    }

    public void Dispose()
    {
        foreach (VulkanBuffer buffer in _instances)
        {
            buffer.Dispose();
        }

        _cubeIndices.Dispose();
        _cubeVertices.Dispose();
        _pipeline.Dispose();
        _staging = [];
    }
}
