using System.Reflection;
using System.Runtime.InteropServices;
using OpenTK.Mathematics;
using Tesseris.Engine.Rendering;
using Silk.NET.Vulkan;
using Tesseris.Engine.Rendering.Vulkan;
using Tesseris.ModApi;

namespace Tesseris.Game.Modding.Client.Rendering;

internal sealed class ModCommandRenderer : IDisposable
{
    private const int MaximumResidentTextures = 512;
    private const int MaximumWarnings = 2_048;
    private const int MaximumVertices = ModRenderGeometryBuilder.DefaultMaximumQuads * 6;
    private const uint PushConstantSize = 16 * sizeof(float);

    private readonly VulkanContext context;
    private readonly VulkanRenderer renderer;
    private readonly VulkanPipeline pipeline;
    private readonly VulkanBuffer[] vertexBuffers;
    private readonly ModRenderAssetIndex assets;
    private readonly ModRenderGeometryBuilder geometryBuilder;
    private readonly Action<string> warning;
    private readonly Dictionary<ResourceId, TextureHandle> textures = [];
    private readonly HashSet<ResourceId> unavailableTextures = [];
    private readonly HashSet<ResourceId> warnedTextures = [];
    private readonly TextureHandle solidTexture;
    private readonly TextureHandle missingTexture;
    private int frameVertices;
    private bool frameOpen;
    private bool warnedGeometryLimit;
    private bool warnedVertexLimit;

    public ModCommandRenderer(
        VulkanContext context,
        VulkanRenderer renderer,
        VulkanSwapchain swapchain,
        ModRenderAssetIndex assets,
        Action<string> warning)
    {
        this.context = context ?? throw new ArgumentNullException(nameof(context));
        this.renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        this.assets = assets ?? throw new ArgumentNullException(nameof(assets));
        this.warning = warning ?? throw new ArgumentNullException(nameof(warning));

        VertexInputAttributeDescription[] attributes =
        [
            new() { Location = 0, Binding = 0, Format = Format.R32G32B32Sfloat, Offset = 0 },
            new() { Location = 1, Binding = 0, Format = Format.R32G32Sfloat, Offset = 3 * sizeof(float) },
            new() { Location = 2, Binding = 0, Format = Format.R32G32B32A32Sfloat, Offset = 5 * sizeof(float) },
        ];
        pipeline = VulkanPipeline.Create(
            context,
            swapchain,
            Assembly.GetExecutingAssembly(),
            "mod_billboard.vert.spv",
            "mod_billboard.frag.spv",
            ModRenderGeometry.FloatsPerVertex * sizeof(float),
            attributes,
            PushConstantSize,
            BlendMode.AlphaBlend,
            CullModeFlags.None,

            // Jeden vzorek: modová geometrie se kreslí až na obrazovku, ne do vyhlazované
            // scény. S vícevzorkovým nastavením by se pipeline neshodla s cílem a ovladač
            // by odmítl celý snímek.
            rasterizationSamples: SampleCountFlags.Count1Bit);

        vertexBuffers = new VulkanBuffer[VulkanRenderer.FramesInFlight];
        for (int index = 0; index < vertexBuffers.Length; index++)
        {
            vertexBuffers[index] = VulkanBuffer.CreateHostVisible(
                context,
                (ulong)(MaximumVertices * ModRenderGeometry.FloatsPerVertex * sizeof(float)),
                BufferUsageFlags.VertexBufferBit);
        }

        solidTexture = CreateTexture([255, 255, 255, 255], 1, 1);
        missingTexture = CreateTexture(MissingPixels(), 16, 16);
        geometryBuilder = new ModRenderGeometryBuilder(assets, warning);
    }

    internal void BeginFrame()
    {
        frameVertices = 0;
        frameOpen = true;
    }

    internal void PreloadTextures(IEnumerable<ResourceId> textureIds)
    {
        ArgumentNullException.ThrowIfNull(textureIds);
        if (frameOpen) throw new InvalidOperationException("Preload mod textures before beginning a frame.");
        foreach (ResourceId id in textureIds.Distinct().OrderBy(value => value.Value, StringComparer.Ordinal))
        {
            _ = ResolveTexture(ModRenderTextureKey.Texture(id));
        }
    }

    internal unsafe void Draw(
        IReadOnlyList<RecordedModRenderCommand> commands,
        IReadOnlyList<ModParticleRenderSnapshot> particles,
        ModRenderViewSnapshot view,
        Matrix4 viewProjection,
        Vector3 cameraRight,
        Vector3 cameraUp,
        RenderStats stats)
    {
        if (!frameOpen) throw new InvalidOperationException("Begin the mod Vulkan frame before drawing.");
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(particles);
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(stats);
        if (!MatrixFinite(ref viewProjection))
            throw new ArgumentOutOfRangeException(nameof(viewProjection), "View-projection matrix must be finite.");

        ModRenderGeometry geometry = geometryBuilder.Build(
            commands,
            particles,
            view.CameraPosition,
            cameraRight,
            cameraUp);
        if (geometry.DroppedQuads > 0 && !warnedGeometryLimit)
        {
            warnedGeometryLimit = true;
            warning($"Dropped {geometry.DroppedQuads} mod render quads after reaching the per-frame geometry limit.");
        }
        if (geometry.Vertices.Length == 0) return;

        int vertexCount = geometry.Vertices.Length / ModRenderGeometry.FloatsPerVertex;
        if (frameVertices + vertexCount > MaximumVertices)
        {
            if (!warnedVertexLimit)
            {
                warnedVertexLimit = true;
                warning("Skipped a mod render phase because the shared per-frame Vulkan vertex buffer is full.");
            }
            return;
        }

        int firstFrameVertex = frameVertices;
        vertexBuffers[renderer.FrameSlot].Write<float>(
            geometry.Vertices,
            firstFrameVertex * ModRenderGeometry.FloatsPerVertex * sizeof(float));
        frameVertices += vertexCount;

        CommandBuffer commandBuffer = renderer.CommandBuffer;
        Vk vk = context.Vk;

        vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, pipeline.Handle);

        Silk.NET.Vulkan.Buffer buffer = vertexBuffers[renderer.FrameSlot].Handle;
        ulong bufferOffset = 0;
        vk.CmdBindVertexBuffers(commandBuffer, 0, 1, &buffer, &bufferOffset);

        Span<float> push = stackalloc float[16];
        MemoryMarshal.Cast<Matrix4, float>(MemoryMarshal.CreateSpan(ref viewProjection, 1)).CopyTo(push);

        fixed (float* pushPtr = push)
        {
            vk.CmdPushConstants(
                commandBuffer, pipeline.Layout,
                ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit,
                0, PushConstantSize, pushPtr);
        }

        foreach (ModRenderBatch batch in geometry.Batches)
        {
            ResolveTexture(batch.Texture).Descriptor.Bind(commandBuffer, pipeline.Layout);
            vk.CmdDraw(
                commandBuffer, (uint)batch.VertexCount, 1,
                (uint)(firstFrameVertex + batch.FirstVertex), 0);
            stats.CountDrawCall();
        }
    }

    internal void EndFrame() => frameOpen = false;

    private TextureHandle ResolveTexture(ModRenderTextureKey key)
    {
        if (key.Solid) return solidTexture;
        if (textures.TryGetValue(key.TextureId, out TextureHandle? existing)) return existing;
        if (unavailableTextures.Contains(key.TextureId)) return missingTexture;
        if (textures.Count >= MaximumResidentTextures)
        {
            WarnTexture(key.TextureId, "resident mod texture limit reached; using missing texture");
            return missingTexture;
        }

        string? path;
        try
        {
            path = assets.ResolveTexture(key.TextureId);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException)
        {
            WarnTexture(key.TextureId, $"resolution failed ({exception.Message}); using missing texture");
            RememberUnavailable(key.TextureId);
            return missingTexture;
        }

        if (path is null)
        {
            WarnTexture(key.TextureId, "asset was not found; using missing texture");
            RememberUnavailable(key.TextureId);
            return missingTexture;
        }

        try
        {
            VulkanTexture texture = VulkanTexture.CreateRgbaFromPng(context, path);
            var created = new TextureHandle(
                texture, new VulkanTextureSet(context, pipeline.DescriptorSetLayout, texture));
            textures.Add(key.TextureId, created);
            return created;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException)
        {
            WarnTexture(key.TextureId, $"could not be uploaded ({exception.Message}); using missing texture");
            RememberUnavailable(key.TextureId);
            return missingTexture;
        }
    }

    private void RememberUnavailable(ResourceId id)
    {
        if (unavailableTextures.Count < MaximumResidentTextures) unavailableTextures.Add(id);
    }

    private TextureHandle CreateTexture(byte[] pixels, int width, int height)
    {
        VulkanTexture texture = VulkanTexture.CreateRgba(context, width, height, pixels);
        return new TextureHandle(texture, new VulkanTextureSet(context, pipeline.DescriptorSetLayout, texture));
    }

    private void WarnTexture(ResourceId id, string reason)
    {
        if (warnedTextures.Count >= MaximumWarnings || !warnedTextures.Add(id)) return;
        warning($"Mod texture '{id}' {reason}.");
    }

    private static byte[] MissingPixels()
    {
        const int size = 16;
        var pixels = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            bool magenta = ((x / 4) + (y / 4)) % 2 == 0;
            int offset = ((y * size) + x) * 4;
            pixels[offset] = magenta ? (byte)255 : (byte)0;
            pixels[offset + 1] = 0;
            pixels[offset + 2] = magenta ? (byte)255 : (byte)0;
            pixels[offset + 3] = 255;
        }

        return pixels;
    }

    private static bool MatrixFinite(ref Matrix4 matrix)
    {
        ReadOnlySpan<float> values = MemoryMarshal.Cast<Matrix4, float>(
            MemoryMarshal.CreateReadOnlySpan(ref matrix, 1));
        foreach (float value in values)
        {
            if (!float.IsFinite(value)) return false;
        }

        return true;
    }

    public void Dispose()
    {
        foreach (TextureHandle texture in textures.Values) texture.Dispose();
        missingTexture.Dispose();
        solidTexture.Dispose();
        foreach (VulkanBuffer buffer in vertexBuffers) buffer.Dispose();
        pipeline.Dispose();
    }

    private sealed class TextureHandle(VulkanTexture texture, VulkanTextureSet descriptor) : IDisposable
    {
        public VulkanTexture Texture { get; } = texture;

        public VulkanTextureSet Descriptor { get; } = descriptor;

        public void Dispose() => Texture.Dispose();
    }
}
