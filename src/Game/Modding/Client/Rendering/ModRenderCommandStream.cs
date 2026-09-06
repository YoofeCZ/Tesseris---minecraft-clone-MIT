using Tesseris.ModApi;

namespace Tesseris.Game.Modding.Client.Rendering;

internal abstract record RecordedModRenderCommand;

internal sealed record RecordedModModel(
    ResourceId ModelId,
    ModTransform Transform,
    ModColor Tint) : RecordedModRenderCommand;

internal sealed record RecordedModBillboard(
    ResourceId TextureId,
    ModVector3 Position,
    float Width,
    float Height,
    ModColor Tint) : RecordedModRenderCommand;

internal sealed record RecordedModLine(
    ModVector3 From,
    ModVector3 To,
    ModColor Color,
    float Width) : RecordedModRenderCommand;

internal sealed class ModRenderCommandStream : IModRenderCommandBuffer
{
    internal const int DefaultMaximumCommandsPerFrame = 8_192;
    private const float MaximumCoordinateMagnitude = 16_777_216f;
    private const float MaximumExtent = 1_000_000f;

    private readonly ModRenderHost host;
    private readonly int maximumCommandsPerFrame;
    private readonly List<RecordedModRenderCommand> commands = [];
    private bool frameOpen;
    private bool recording;
    private int frameCommandCount;

    public ModRenderCommandStream(
        ModRenderHost host,
        int maximumCommandsPerFrame = DefaultMaximumCommandsPerFrame)
    {
        this.host = host ?? throw new ArgumentNullException(nameof(host));
        if (maximumCommandsPerFrame <= 0) throw new ArgumentOutOfRangeException(nameof(maximumCommandsPerFrame));
        this.maximumCommandsPerFrame = maximumCommandsPerFrame;
    }

    internal int FrameCommandCount => frameCommandCount;

    internal void BeginFrame()
    {
        if (recording) throw new InvalidOperationException("Cannot begin a render frame during callback recording.");
        frameOpen = true;
        frameCommandCount = 0;
        commands.Clear();
    }

    internal IReadOnlyList<RecordedModRenderCommand> RecordPhase(
        ModRenderPhase phase,
        ModRenderViewSnapshot view)
    {
        if (!frameOpen) throw new InvalidOperationException("Begin the mod render frame before dispatching a phase.");
        if (recording) throw new InvalidOperationException("Mod render callback dispatch is not reentrant.");
        ValidateView(view);
        commands.Clear();
        recording = true;
        try
        {
            host.Dispatch(phase, view, this);
            return Array.AsReadOnly(commands.ToArray());
        }
        finally
        {
            recording = false;
            commands.Clear();
        }
    }

    internal void EndFrame()
    {
        if (recording) throw new InvalidOperationException("Cannot end a render frame during callback recording.");
        frameOpen = false;
        commands.Clear();
    }

    public void DrawModel(ResourceId modelId, ModTransform transform, ModColor tint)
    {
        EnsureRecording();
        ValidateId(modelId, nameof(modelId));
        if (!FinitePosition(transform.Position) || !FiniteExtent(transform.Scale) || !Finite(transform.Rotation))
            throw new ArgumentOutOfRangeException(nameof(transform), "Model transform must be finite.");
        Add(new RecordedModModel(modelId, transform, tint));
    }

    public void DrawBillboard(ResourceId textureId, ModVector3 position, float width, float height, ModColor tint)
    {
        EnsureRecording();
        ValidateId(textureId, nameof(textureId));
        if (!FinitePosition(position)) throw new ArgumentOutOfRangeException(nameof(position));
        ValidatePositiveFinite(width, nameof(width));
        ValidatePositiveFinite(height, nameof(height));
        Add(new RecordedModBillboard(textureId, position, width, height, tint));
    }

    public void DrawLine(ModVector3 from, ModVector3 to, ModColor color, float width)
    {
        EnsureRecording();
        if (!FinitePosition(from)) throw new ArgumentOutOfRangeException(nameof(from));
        if (!FinitePosition(to)) throw new ArgumentOutOfRangeException(nameof(to));
        ValidatePositiveFinite(width, nameof(width));
        float dx = to.X - from.X;
        float dy = to.Y - from.Y;
        float dz = to.Z - from.Z;
        if ((dx * dx) + (dy * dy) + (dz * dz) <= 1e-12f)
            throw new ArgumentException("A rendered line must have non-zero length.", nameof(to));
        Add(new RecordedModLine(from, to, color, width));
    }

    private void Add(RecordedModRenderCommand command)
    {
        if (frameCommandCount >= maximumCommandsPerFrame)
            throw new InvalidOperationException(
                $"Mod render command limit of {maximumCommandsPerFrame} per frame was exceeded.");
        frameCommandCount++;
        commands.Add(command);
    }

    private void EnsureRecording()
    {
        if (!recording)
            throw new InvalidOperationException("The mod render command buffer is only valid during its callback.");
    }

    private static void ValidateId(ResourceId id, string parameter)
    {
        if (string.IsNullOrWhiteSpace(id.Value))
            throw new ArgumentException("A non-default resource ID is required.", parameter);
    }

    private static void ValidatePositiveFinite(float value, string parameter)
    {
        if (!float.IsFinite(value) || value <= 0f || value > MaximumExtent)
            throw new ArgumentOutOfRangeException(parameter, value, "Value must be finite and positive.");
    }

    private static bool FinitePosition(ModVector3 value) =>
        Finite(value) && MaximumAbsolute(value) <= MaximumCoordinateMagnitude;

    private static bool FiniteExtent(ModVector3 value) =>
        Finite(value) && MaximumAbsolute(value) <= MaximumExtent;

    private static bool Finite(ModVector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static bool Finite(ModQuaternion value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y)
        && float.IsFinite(value.Z) && float.IsFinite(value.W)
        && MathF.Max(MathF.Abs(value.X), MathF.Max(MathF.Abs(value.Y), MathF.Max(MathF.Abs(value.Z), MathF.Abs(value.W))))
            <= MaximumExtent;

    private static float MaximumAbsolute(ModVector3 value) =>
        MathF.Max(MathF.Abs(value.X), MathF.Max(MathF.Abs(value.Y), MathF.Abs(value.Z)));

    private static void ValidateView(ModRenderViewSnapshot view)
    {
        ArgumentNullException.ThrowIfNull(view);
        if (!FinitePosition(view.CameraPosition) || !Finite(view.CameraRotation)
            || view.ViewportWidth <= 0 || view.ViewportHeight <= 0
            || !float.IsFinite(view.PartialTick))
            throw new ArgumentOutOfRangeException(nameof(view), "Render view values must be finite and dimensions positive.");
    }
}
