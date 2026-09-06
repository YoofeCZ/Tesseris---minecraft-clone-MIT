using OpenTK.Mathematics;
using Tesseris.Engine.Core;
using Tesseris.Engine.Rendering;
using Tesseris.Engine.Rendering.Vulkan;
using Tesseris.Game.Content;
using Tesseris.ModApi;
using ModResourceId = Tesseris.ModApi.ResourceId;

namespace Tesseris.Game.Modding.Client.Rendering;

/// <summary>Game-internal adapter; no Vulkan or OpenTK type crosses the public ModApi boundary.</summary>
internal sealed class GameModRenderBridge : IDisposable
{
    internal static readonly IReadOnlyList<ModRenderPhase> OrderedPhases =
        Array.AsReadOnly(Enum.GetValues<ModRenderPhase>().Order().ToArray());

    private readonly ModClientPlatform platform;
    private readonly ModRenderCommandStream commands;
    private readonly ModParticleSimulation particles = new();
    private readonly ModCommandRenderer renderer;
    private readonly IReadOnlyDictionary<ModResourceId, ModParticleDefinition> particleDefinitions;
    private readonly Action<string> warning;
    private bool frameOpen;

    internal GameModRenderBridge(
        ModClientPlatform platform,
        ContentCatalog catalog,
        ContentAssetResolver assets,
        VulkanContext context,
        VulkanRenderer renderer,
        VulkanSwapchain swapchain,
        Action<string>? warning = null)
    {
        this.platform = platform ?? throw new ArgumentNullException(nameof(platform));
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(assets);
        if (!platform.IsAvailable) throw new InvalidOperationException("The client render bridge is unavailable in headless mode.");
        if (!platform.IsFrozen) throw new InvalidOperationException("Freeze the client platform before creating its render bridge.");
        this.warning = warning ?? Log.Warn;
        commands = new ModRenderCommandStream(platform.Rendering);
        particleDefinitions = platform.Particles.Definitions
            .ToDictionary(item => item.Definition.Id, item => item.Definition);
        var assetIndex = new ModRenderAssetIndex(catalog, assets);
        this.renderer = new ModCommandRenderer(context, renderer, swapchain, assetIndex, this.warning);
        this.renderer.PreloadTextures(particleDefinitions.Values.Select(definition => definition.TextureId));
    }

    internal void BeginFrame()
    {
        if (frameOpen) throw new InvalidOperationException("The mod render frame is already open.");
        frameOpen = true;
        commands.BeginFrame();
        renderer.BeginFrame();
    }

    internal static ModRenderViewSnapshot CreateViewSnapshot(
        Camera camera,
        int viewportWidth,
        int viewportHeight,
        float partialTick)
    {
        ArgumentNullException.ThrowIfNull(camera);
        if (viewportWidth <= 0) throw new ArgumentOutOfRangeException(nameof(viewportWidth));
        if (viewportHeight <= 0) throw new ArgumentOutOfRangeException(nameof(viewportHeight));
        if (!float.IsFinite(partialTick)) throw new ArgumentOutOfRangeException(nameof(partialTick));

        float relativeYaw = MathHelper.DegreesToRadians(-(camera.YawDegrees + 90f));
        float pitch = MathHelper.DegreesToRadians(camera.PitchDegrees);
        Quaternion orientation = Quaternion.Normalize(
            Quaternion.FromAxisAngle(Vector3.UnitY, relativeYaw)
            * Quaternion.FromAxisAngle(Vector3.UnitX, pitch));
        return new ModRenderViewSnapshot(
            new ModVector3(camera.Position.X, camera.Position.Y, camera.Position.Z),
            new ModQuaternion(orientation.X, orientation.Y, orientation.Z, orientation.W),
            viewportWidth,
            viewportHeight,
            partialTick);
    }

    internal void Update(TimeSpan delta)
    {
        IReadOnlyList<ModParticleSpawn> spawns = platform.Particles.DrainSpawns();
        int accepted = particles.Update(delta, particleDefinitions, spawns);
        if (accepted < spawns.Count)
            warning($"Dropped {spawns.Count - accepted} mod particle spawns because an active/update limit was reached.");
    }

    internal void DrawPhase(
        ModRenderPhase phase,
        ModRenderViewSnapshot view,
        Matrix4 viewProjection,
        Vector3 cameraRight,
        Vector3 cameraUp,
        RenderStats stats)
    {
        if (!frameOpen) throw new InvalidOperationException("Begin the mod render frame before drawing phases.");
        if (!Enum.IsDefined(phase)) throw new ArgumentOutOfRangeException(nameof(phase));
        IReadOnlyList<RecordedModRenderCommand> recorded = commands.RecordPhase(phase, view);
        IReadOnlyList<ModParticleRenderSnapshot> particleSnapshot = phase == ModRenderPhase.TransparentWorld
            ? particles.Snapshot()
            : [];
        renderer.Draw(recorded, particleSnapshot, view, viewProjection, cameraRight, cameraUp, stats);
    }

    internal void EndFrame()
    {
        if (!frameOpen) return;
        renderer.EndFrame();
        commands.EndFrame();
        frameOpen = false;
    }

    public void Dispose()
    {
        EndFrame();
        particles.Clear();
        renderer.Dispose();
    }
}
