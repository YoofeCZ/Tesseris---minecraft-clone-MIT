using System.Reflection;
using System.Runtime.InteropServices;
using ImGuiNET;
using Silk.NET.Vulkan;

namespace Tesseris.Engine.Rendering.Vulkan;

/// <summary>
/// Vykreslování ImGui do Vulkanu.
/// </summary>
/// <remarks>
/// <para><b>Proč to musí být ručně.</b> ImGui.NET je jen obal nad knihovnou Dear ImGui —
/// vyrábí seznam trojúhelníků a nůžkových obdélníků, ale kreslení neřeší. Backend si musí
/// napsat každý sám pro své API.</para>
///
/// <para><b>Proč vůbec ImGui.</b> UI se má dělat immediate-mode knihovnou,
/// protože vlastní retained-mode UI je nejčastější místo, kde vlastní engine sežere rok.
/// Režim velitele potřebuje přehledy zásob a propustnosti — tabulky, posuvníky, klikání —
/// a to je přesně ta práce, které se sekce 7 chce vyhnout.</para>
///
/// <para><b>Buffery na snímek v letu.</b> Geometrie panelů se každý snímek přepisuje, takže
/// zapisovat do bufferu, ze kterého grafika ještě čte, je chyba, kterou ovladač hlásí.</para>
///
/// <para><b>Nůžky jsou povinné, ne kosmetika.</b> ImGui na nich staví ořezávání seznamů
/// a posuvných oblastí — bez nich by obsah přetékal přes okraje panelu.</para>
/// </remarks>
public sealed unsafe class ImGuiVulkanRenderer : IDisposable
{
    /// <summary>Dva vektory po dvou floatech: měřítko a posun.</summary>
    private const uint PushConstantSize = 4 * sizeof(float);

    private const int InitialVertices = 8192;
    private const int InitialIndices = 16384;

    private readonly VulkanContext _context;
    private readonly VulkanRenderer _renderer;
    private readonly VulkanPipeline _pipeline;
    private readonly VulkanTexture _fontTexture;
    private readonly VulkanTextureSet _fontDescriptor;
    private readonly VulkanBuffer[] _vertices;
    private readonly VulkanBuffer[] _indices;
    private readonly int[] _vertexCapacity;
    private readonly int[] _indexCapacity;

    private readonly nint _imguiContext;

    public ImGuiVulkanRenderer(
        VulkanContext context,
        VulkanRenderer renderer,
        VulkanSwapchain swapchain,
        Assembly shaderAssembly)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(swapchain);

        _context = context;
        _renderer = renderer;

        _imguiContext = ImGui.CreateContext();
        ImGui.SetCurrentContext(_imguiContext);

        ImGuiIOPtr io = ImGui.GetIO();
        io.BackendFlags |= ImGuiBackendFlags.RendererHasVtxOffset;

        // Bez souboru s nastavením: hra si polohu panelů řídí sama a psát vedle binárky
        // imgui.ini je nechtěné.
        io.ConfigFlags |= ImGuiConfigFlags.NoMouseCursorChange;
        unsafe
        {
            io.NativePtr->IniFilename = null;
        }

        // ImGui má vlastní vertex formát a musí sedět bajt po bajtu: dvě dvojice floatů
        // a jeden zabalený RGBA bajt na kanál.
        VertexInputAttributeDescription[] attributes =
        [
            new() { Location = 0, Binding = 0, Format = Format.R32G32Sfloat, Offset = 0 },
            new() { Location = 1, Binding = 0, Format = Format.R32G32Sfloat, Offset = 8 },
            new() { Location = 2, Binding = 0, Format = Format.R8G8B8A8Unorm, Offset = 16 },
        ];

        _pipeline = VulkanPipeline.Create(
            context,
            swapchain,
            shaderAssembly,
            "imgui.vert.spv",
            "imgui.frag.spv",
            (uint)sizeof(ImDrawVert),
            attributes,
            PushConstantSize,
            BlendMode.AlphaBlend,
            CullModeFlags.None,

            // Panely jdou až na obrazovku, ne do vyhlazované scény — jeden vzorek.
            rasterizationSamples: SampleCountFlags.Count1Bit);

        _fontTexture = BuildFontTexture(io);
        _fontDescriptor = new VulkanTextureSet(
            context, _pipeline.DescriptorSetLayout, [VulkanTextureSet.Entry.From(_fontTexture)]);

        int frames = VulkanRenderer.FramesInFlight;
        _vertices = new VulkanBuffer[frames];
        _indices = new VulkanBuffer[frames];
        _vertexCapacity = new int[frames];
        _indexCapacity = new int[frames];

        for (int i = 0; i < frames; i++)
        {
            GrowVertices(i, InitialVertices);
            GrowIndices(i, InitialIndices);
        }
    }

    /// <summary>Kolik draw callů vydalo poslední kreslení panelů.</summary>
    public int LastDrawCalls { get; private set; }

    /// <summary>Chce ImGui zrovna myš? Když ano, hra na kliknutí reagovat nemá.</summary>
    public static bool WantsMouse => ImGui.GetIO().WantCaptureMouse;

    /// <summary>Chce ImGui zrovna klávesnici?</summary>
    public static bool WantsKeyboard => ImGui.GetIO().WantCaptureKeyboard;

    /// <summary>Začne nový snímek panelů.</summary>
    public void BeginFrame(int width, int height, float deltaSeconds, MouseSnapshot mouse)
    {
        ImGui.SetCurrentContext(_imguiContext);
        ImGuiIOPtr io = ImGui.GetIO();

        io.DisplaySize = new System.Numerics.Vector2(Math.Max(width, 1), Math.Max(height, 1));
        io.DisplayFramebufferScale = new System.Numerics.Vector2(1f, 1f);

        // Nula by ImGui rozhodila animace a časovače; jedna šedesátina je bezpečná náhrada.
        io.DeltaTime = deltaSeconds > 0f ? deltaSeconds : 1f / 60f;

        io.MousePos = new System.Numerics.Vector2(mouse.X, mouse.Y);
        io.MouseDown[0] = mouse.Left;
        io.MouseDown[1] = mouse.Right;
        io.MouseDown[2] = mouse.Middle;
        io.MouseWheel = mouse.Wheel;

        ImGui.NewFrame();
    }

    /// <summary>Uzavře snímek a vykreslí panely.</summary>
    public void Render(CommandBuffer commandBuffer)
    {
        ImGui.SetCurrentContext(_imguiContext);
        ImGui.Render();

        LastDrawCalls = 0;
        ImDrawDataPtr data = ImGui.GetDrawData();

        if (data.CmdListsCount == 0 || data.TotalVtxCount == 0)
        {
            return;
        }

        int frame = _renderer.FrameSlot % _vertices.Length;
        UploadGeometry(frame, data);

        Vk vk = _context.Vk;
        vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, _pipeline.Handle);
        _fontDescriptor.Bind(commandBuffer, _pipeline.Layout);

        Span<float> push =
        [
            2f / data.DisplaySize.X,
            2f / data.DisplaySize.Y,
            -1f,
            -1f,
        ];

        // OBA STUPNĚ, i když je čte jen vrcholový. Layout pipeline deklaruje rozsah pro
        // vertex i fragment, a Vulkan chce, aby zápis pokryl všechny stupně, které rozsah
        // vyhlašuje — jinak hlásí VUID-vkCmdPushConstants-offset-01796.
        fixed (float* pushPtr = push)
        {
            vk.CmdPushConstants(
                commandBuffer, _pipeline.Layout,
                ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, 0, PushConstantSize, pushPtr);
        }

        Silk.NET.Vulkan.Buffer vertexBuffer = _vertices[frame].Handle;
        ulong offset = 0;
        vk.CmdBindVertexBuffers(commandBuffer, 0, 1, &vertexBuffer, &offset);
        vk.CmdBindIndexBuffer(commandBuffer, _indices[frame].Handle, 0, IndexType.Uint16);

        int vertexOffset = 0;
        uint indexOffset = 0;

        for (int list = 0; list < data.CmdListsCount; list++)
        {
            ImDrawListPtr commands = data.CmdLists[list];

            for (int i = 0; i < commands.CmdBuffer.Size; i++)
            {
                ImDrawCmdPtr command = commands.CmdBuffer[i];

                // NŮŽKY. ImGui na nich staví ořezávání seznamů a posuvných oblastí;
                // bez nich by obsah přetékal přes okraje panelu.
                var scissor = new Rect2D
                {
                    Offset = new Offset2D
                    {
                        X = Math.Max((int)command.ClipRect.X, 0),
                        Y = Math.Max((int)command.ClipRect.Y, 0),
                    },
                    Extent = new Extent2D
                    {
                        Width = (uint)Math.Max(command.ClipRect.Z - command.ClipRect.X, 0f),
                        Height = (uint)Math.Max(command.ClipRect.W - command.ClipRect.Y, 0f),
                    },
                };

                vk.CmdSetScissor(commandBuffer, 0, 1, &scissor);
                vk.CmdDrawIndexed(
                    commandBuffer,
                    command.ElemCount,
                    1,
                    indexOffset + command.IdxOffset,
                    vertexOffset + (int)command.VtxOffset,
                    0);

                LastDrawCalls++;
            }

            vertexOffset += commands.VtxBuffer.Size;
            indexOffset += (uint)commands.IdxBuffer.Size;
        }
    }

    private void UploadGeometry(int frame, ImDrawDataPtr data)
    {
        if (data.TotalVtxCount > _vertexCapacity[frame])
        {
            GrowVertices(frame, data.TotalVtxCount * 2);
        }

        if (data.TotalIdxCount > _indexCapacity[frame])
        {
            GrowIndices(frame, data.TotalIdxCount * 2);
        }

        int vertexBytes = 0;
        int indexBytes = 0;

        for (int list = 0; list < data.CmdListsCount; list++)
        {
            ImDrawListPtr commands = data.CmdLists[list];

            int listVertexBytes = commands.VtxBuffer.Size * sizeof(ImDrawVert);
            int listIndexBytes = commands.IdxBuffer.Size * sizeof(ushort);

            _vertices[frame].Write(
                new ReadOnlySpan<byte>((void*)commands.VtxBuffer.Data, listVertexBytes), vertexBytes);
            _indices[frame].Write(
                new ReadOnlySpan<byte>((void*)commands.IdxBuffer.Data, listIndexBytes), indexBytes);

            vertexBytes += listVertexBytes;
            indexBytes += listIndexBytes;
        }
    }

    private void GrowVertices(int frame, int vertices)
    {
        _vertices[frame]?.Dispose();
        _vertices[frame] = VulkanBuffer.CreateHostVisible(
            _context, (ulong)(vertices * sizeof(ImDrawVert)), BufferUsageFlags.VertexBufferBit);
        _vertexCapacity[frame] = vertices;
    }

    private void GrowIndices(int frame, int indices)
    {
        _indices[frame]?.Dispose();
        _indices[frame] = VulkanBuffer.CreateHostVisible(
            _context, (ulong)(indices * sizeof(ushort)), BufferUsageFlags.IndexBufferBit);
        _indexCapacity[frame] = indices;
    }

    private VulkanTexture BuildFontTexture(ImGuiIOPtr io)
    {
        io.Fonts.GetTexDataAsRGBA32(out nint pixels, out int width, out int height, out _);

        var managed = new byte[width * height * 4];
        Marshal.Copy(pixels, managed, 0, managed.Length);

        VulkanTexture texture = VulkanTexture.CreateRgba(_context, width, height, managed);
        io.Fonts.SetTexID(1);
        io.Fonts.ClearTexData();
        return texture;
    }

    public void Dispose()
    {
        foreach (VulkanBuffer buffer in _vertices)
        {
            buffer?.Dispose();
        }

        foreach (VulkanBuffer buffer in _indices)
        {
            buffer?.Dispose();
        }

        _fontDescriptor.Dispose();
        _fontTexture.Dispose();
        _pipeline.Dispose();

        ImGui.DestroyContext(_imguiContext);
    }
}

/// <summary>Stav myši pro ImGui. Okno ho vyplní ze svého vstupu.</summary>
public readonly record struct MouseSnapshot(float X, float Y, bool Left, bool Right, bool Middle, float Wheel);
