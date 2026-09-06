using OpenTK.Mathematics;
using Tesseris.ModApi;

namespace Tesseris.Game.Modding.Client.Rendering;

internal readonly record struct ModRenderTextureKey(bool Solid, ResourceId TextureId)
{
    public static ModRenderTextureKey SolidColor => new(true, default);
    public static ModRenderTextureKey Texture(ResourceId id) => new(false, id);
}

internal readonly record struct ModRenderBatch(ModRenderTextureKey Texture, int FirstVertex, int VertexCount);

internal sealed record ModRenderGeometry(float[] Vertices, IReadOnlyList<ModRenderBatch> Batches, int DroppedQuads)
{
    internal const int FloatsPerVertex = 9;
}

internal sealed class ModRenderGeometryBuilder
{
    internal const int DefaultMaximumQuads = 32_768;
    private const int MaximumModelWarnings = 2_048;

    private readonly int maximumQuads;
    private readonly ModRenderAssetIndex assets;
    private readonly Action<string> warning;
    private readonly HashSet<ResourceId> warnedModels = [];
    private readonly List<float> vertices = [];
    private readonly List<ModRenderBatch> batches = [];
    private int dropped;

    public ModRenderGeometryBuilder(
        ModRenderAssetIndex assets,
        Action<string> warning,
        int maximumQuads = DefaultMaximumQuads)
    {
        this.assets = assets ?? throw new ArgumentNullException(nameof(assets));
        this.warning = warning ?? throw new ArgumentNullException(nameof(warning));
        if (maximumQuads <= 0) throw new ArgumentOutOfRangeException(nameof(maximumQuads));
        this.maximumQuads = maximumQuads;
    }

    internal ModRenderGeometry Build(
        IReadOnlyList<RecordedModRenderCommand> commands,
        IReadOnlyList<ModParticleRenderSnapshot> particles,
        ModVector3 cameraPosition,
        Vector3 cameraRight,
        Vector3 cameraUp)
    {
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(particles);
        vertices.Clear();
        batches.Clear();
        dropped = 0;

        Vector3 camera = Vector(cameraPosition);
        cameraRight = NormalizeBasis(cameraRight, Vector3.UnitX);
        cameraUp = NormalizeBasis(cameraUp, Vector3.UnitY);

        foreach (RecordedModRenderCommand command in commands)
        {
            switch (command)
            {
                case RecordedModBillboard billboard:
                    AddBillboard(
                        ModRenderTextureKey.Texture(billboard.TextureId),
                        Vector(billboard.Position),
                        billboard.Width,
                        billboard.Height,
                        Color(billboard.Tint),
                        cameraRight,
                        cameraUp);
                    break;
                case RecordedModLine line:
                    AddLine(Vector(line.From), Vector(line.To), line.Width, Color(line.Color), camera, cameraRight, cameraUp);
                    break;
                case RecordedModModel model:
                    AddModelPlaceholder(model, camera, cameraRight, cameraUp);
                    break;
            }
        }

        foreach (ModParticleRenderSnapshot particle in particles)
        {
            AddBillboard(
                ModRenderTextureKey.Texture(particle.TextureId),
                Vector(particle.Position),
                particle.Size,
                particle.Size,
                Color(particle.Color),
                cameraRight,
                cameraUp);
        }

        return new ModRenderGeometry(
            vertices.ToArray(),
            Array.AsReadOnly(batches.ToArray()),
            dropped);
    }

    private void AddModelPlaceholder(
        RecordedModModel model,
        Vector3 camera,
        Vector3 cameraRight,
        Vector3 cameraUp)
    {
        bool exists = assets.ResolveModel(model.ModelId) is not null;
        if (warnedModels.Count < MaximumModelWarnings && warnedModels.Add(model.ModelId))
        {
            warning(exists
                ? $"Mod model '{model.ModelId}' uses a visible placeholder because the existing item/block GPU batches cannot be safely rebound per arbitrary transform yet."
                : $"Mod model '{model.ModelId}' was not found; drawing the visible missing-model placeholder.");
        }

        Vector4 color = exists ? Color(model.Tint) : new Vector4(1f, 0f, 1f, 1f);
        color.W = MathF.Max(color.W, 0.65f);
        Vector3 scale = Vector(model.Transform.Scale);
        scale.X = VisibleScale(scale.X);
        scale.Y = VisibleScale(scale.Y);
        scale.Z = VisibleScale(scale.Z);
        var rotation = new Quaternion(
            model.Transform.Rotation.X,
            model.Transform.Rotation.Y,
            model.Transform.Rotation.Z,
            model.Transform.Rotation.W);
        rotation = rotation.LengthSquared < 1e-8f ? Quaternion.Identity : Quaternion.Normalize(rotation);
        Vector3 translation = Vector(model.Transform.Position);
        float lineWidth = MathF.Max(0.01f, MathF.Max(MathF.Abs(scale.X), MathF.Max(MathF.Abs(scale.Y), MathF.Abs(scale.Z))) * 0.025f);

        Span<Vector3> corners = stackalloc Vector3[8];
        for (int index = 0; index < corners.Length; index++)
        {
            var local = new Vector3(
                (index & 1) == 0 ? -0.5f : 0.5f,
                (index & 2) == 0 ? -0.5f : 0.5f,
                (index & 4) == 0 ? -0.5f : 0.5f);
            local *= scale;
            corners[index] = translation + Vector3.Transform(local, rotation);
        }

        ReadOnlySpan<(int A, int B)> edges =
        [
            (0, 1), (1, 3), (3, 2), (2, 0),
            (4, 5), (5, 7), (7, 6), (6, 4),
            (0, 4), (1, 5), (2, 6), (3, 7),
        ];
        foreach ((int a, int b) in edges)
        {
            AddLine(corners[a], corners[b], lineWidth, color, camera, cameraRight, cameraUp);
        }
    }

    private void AddLine(
        Vector3 from,
        Vector3 to,
        float width,
        Vector4 color,
        Vector3 camera,
        Vector3 cameraRight,
        Vector3 cameraUp)
    {
        Vector3 direction = to - from;
        if (direction.LengthSquared < 1e-12f)
        {
            return;
        }

        direction.Normalize();
        Vector3 midpoint = (from + to) * 0.5f;
        Vector3 toCamera = camera - midpoint;
        if (toCamera.LengthSquared < 1e-12f) toCamera = -Vector3.Cross(cameraRight, cameraUp);
        else toCamera.Normalize();
        Vector3 side = Vector3.Cross(direction, toCamera);
        if (side.LengthSquared < 1e-8f) side = Vector3.Cross(direction, cameraUp);
        if (side.LengthSquared < 1e-8f) side = cameraRight;
        side.Normalize();
        side *= width * 0.5f;
        AddQuad(
            ModRenderTextureKey.SolidColor,
            from - side,
            from + side,
            to + side,
            to - side,
            color);
    }

    private void AddBillboard(
        ModRenderTextureKey texture,
        Vector3 position,
        float width,
        float height,
        Vector4 color,
        Vector3 right,
        Vector3 up)
    {
        Vector3 halfRight = right * (width * 0.5f);
        Vector3 halfUp = up * (height * 0.5f);
        AddQuad(
            texture,
            position - halfRight - halfUp,
            position + halfRight - halfUp,
            position + halfRight + halfUp,
            position - halfRight + halfUp,
            color);
    }

    private void AddQuad(
        ModRenderTextureKey texture,
        Vector3 p0,
        Vector3 p1,
        Vector3 p2,
        Vector3 p3,
        Vector4 color)
    {
        if (!Finite(p0) || !Finite(p1) || !Finite(p2) || !Finite(p3))
        {
            dropped++;
            return;
        }

        if (vertices.Count / (6 * ModRenderGeometry.FloatsPerVertex) >= maximumQuads)
        {
            dropped++;
            return;
        }

        int firstVertex = vertices.Count / ModRenderGeometry.FloatsPerVertex;
        Vertex(p0, 0f, 1f, color);
        Vertex(p1, 1f, 1f, color);
        Vertex(p2, 1f, 0f, color);
        Vertex(p0, 0f, 1f, color);
        Vertex(p2, 1f, 0f, color);
        Vertex(p3, 0f, 0f, color);

        if (batches.Count > 0 && batches[^1].Texture == texture)
        {
            ModRenderBatch previous = batches[^1];
            batches[^1] = previous with { VertexCount = previous.VertexCount + 6 };
        }
        else
        {
            batches.Add(new ModRenderBatch(texture, firstVertex, 6));
        }
    }

    private void Vertex(Vector3 position, float u, float v, Vector4 color)
    {
        vertices.Add(position.X);
        vertices.Add(position.Y);
        vertices.Add(position.Z);
        vertices.Add(u);
        vertices.Add(v);
        vertices.Add(color.X);
        vertices.Add(color.Y);
        vertices.Add(color.Z);
        vertices.Add(color.W);
    }

    private static Vector3 Vector(ModVector3 value) => new(value.X, value.Y, value.Z);

    private static Vector4 Color(ModColor value) =>
        new(value.R / 255f, value.G / 255f, value.B / 255f, value.A / 255f);

    private static Vector3 NormalizeBasis(Vector3 value, Vector3 fallback) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z)
        && value.LengthSquared > 1e-8f
            ? Vector3.Normalize(value)
            : fallback;

    private static float VisibleScale(float value) =>
        MathF.Abs(value) >= 0.05f ? value : value < 0f ? -0.05f : 0.05f;

    private static bool Finite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
