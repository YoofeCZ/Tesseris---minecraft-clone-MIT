using System.Text.Json;
using OpenTK.Mathematics;

namespace Tesseris.Game.World;

/// <summary>
/// Čte kvádrový model ve formátu <c>.blockymodel</c> používaném Hytale editory.
/// </summary>
/// <remarks>
/// Není to převod na minecraftovou kostku. Jeden kotvící voxel může nést libovolný strom
/// uzlů, jehož díly přesahují do všech stran i nad sousední bloky. Formát používá 32 jednotek
/// na blok, proto model o velikosti 32 × 32 × 32 sedí přesně na jeden voxel Tesseris.
/// </remarks>
public sealed class HytaleBlockModel
{
    /// <summary>Kolik jednotek formátu odpovídá délce jednoho bloku ve světě.</summary>
    public const float UnitsPerBlock = 32f;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private HytaleBlockModel(IReadOnlyList<Node> nodes)
    {
        Nodes = nodes;

        Vector3 min = new(float.PositiveInfinity);
        Vector3 max = new(float.NegativeInfinity);

        foreach (Node node in nodes)
        {
            if (node.Box is not { } box)
            {
                continue;
            }

            foreach (Vector3 corner in Corners(box))
            {
                min = Vector3.ComponentMin(min, corner);
                max = Vector3.ComponentMax(max, corner);
            }
        }

        Bounds = new Engine.MathLib.Aabb(min, max);
    }

    /// <summary>Všechny uzly v pořadí rodič → potomek, s transformací už ve světové soustavě modelu.</summary>
    public IReadOnlyList<Node> Nodes { get; }

    public Engine.MathLib.Aabb Bounds { get; }

    public IReadOnlyList<ItemShape.Face> BuildFaces(int textureWidth, int textureHeight)
    {
        if (textureWidth <= 0 || textureHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(textureWidth));
        }

        var faces = new List<ItemShape.Face>();

        foreach (Node node in Nodes)
        {
            if (node.Box is not { } box)
            {
                continue;
            }

            Vector3 h = box.HalfSize;
            Vector3 P(float x, float y, float z) =>
                box.Centre + Vector3.Transform(new Vector3(x, y, z), box.Orientation);

            Add("front", P(-h.X, -h.Y, h.Z), P(h.X, -h.Y, h.Z), P(h.X, h.Y, h.Z), P(-h.X, h.Y, h.Z),
                h.X * 2f, h.Y * 2f, Vector3.Transform(Vector3.UnitZ, box.Orientation));
            Add("back", P(h.X, -h.Y, -h.Z), P(-h.X, -h.Y, -h.Z), P(-h.X, h.Y, -h.Z), P(h.X, h.Y, -h.Z),
                h.X * 2f, h.Y * 2f, Vector3.Transform(-Vector3.UnitZ, box.Orientation));
            Add("left", P(-h.X, -h.Y, -h.Z), P(-h.X, -h.Y, h.Z), P(-h.X, h.Y, h.Z), P(-h.X, h.Y, -h.Z),
                h.Z * 2f, h.Y * 2f, Vector3.Transform(-Vector3.UnitX, box.Orientation));
            Add("right", P(h.X, -h.Y, h.Z), P(h.X, -h.Y, -h.Z), P(h.X, h.Y, -h.Z), P(h.X, h.Y, h.Z),
                h.Z * 2f, h.Y * 2f, Vector3.Transform(Vector3.UnitX, box.Orientation));
            Add("top", P(-h.X, h.Y, h.Z), P(h.X, h.Y, h.Z), P(h.X, h.Y, -h.Z), P(-h.X, h.Y, -h.Z),
                h.X * 2f, h.Z * 2f, Vector3.Transform(Vector3.UnitY, box.Orientation));
            Add("bottom", P(-h.X, -h.Y, -h.Z), P(h.X, -h.Y, -h.Z), P(h.X, -h.Y, h.Z), P(-h.X, -h.Y, h.Z),
                h.X * 2f, h.Z * 2f, Vector3.Transform(-Vector3.UnitY, box.Orientation));

            void Add(
                string name, Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3,
                float widthBlocks, float heightBlocks, Vector3 normal)
            {
                TextureFace layout = box.TextureLayout.TryGetValue(name, out TextureFace value)
                    ? value
                    : default;
                float width = widthBlocks * UnitsPerBlock;
                float height = heightBlocks * UnitsPerBlock;
                Vector2[] uv = TextureCoordinates(layout, width, height, textureWidth, textureHeight);
                float shade = ShadeForNormal(normal);

                faces.Add(new ItemShape.Face(
                    p0, p1, p2, p3,
                    uv[0], uv[1], uv[2], uv[3],
                    shade, -1));
            }
        }

        return faces;
    }

    /// <summary>Přečte model ze souboru. Vadný soubor vypíše chybu a vrátí <c>null</c>.</summary>
    public static HytaleBlockModel? TryLoad(string path)
    {
        try
        {
            return Parse(File.ReadAllText(path));
        }
        catch (Exception error) when (error is IOException or JsonException or InvalidDataException)
        {
            Engine.Core.Log.Warn($"Hytale model {path} se nepodarilo nacist: {error.Message}");
            return null;
        }
    }

    /// <summary>Přečte model z JSON. Veřejné kvůli importním nástrojům a testům.</summary>
    public static HytaleBlockModel Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        Document? document = JsonSerializer.Deserialize<Document>(json, Options);

        if (document?.Nodes is not { Length: > 0 })
        {
            throw new InvalidDataException("Blocky model nema zadne uzly.");
        }

        var nodes = new List<Node>();

        foreach (NodeDocument root in document.Nodes)
        {
            Flatten(root, Vector3.Zero, Quaternion.Identity, Vector3.Zero, nodes);
        }

        if (nodes.All(node => node.Box is null))
        {
            throw new InvalidDataException("Blocky model nema jediny kvadr.");
        }

        return new HytaleBlockModel(nodes);
    }

    private static void Flatten(
        NodeDocument source,
        Vector3 parentPosition,
        Quaternion parentOrientation,
        Vector3 parentShapeOffset,
        List<Node> result)
    {
        Quaternion localOrientation = QuaternionOf(source.Orientation);
        Vector3 localPosition = VectorOf(source.Position) / UnitsPerBlock;
        Vector3 position = parentPosition
            + Vector3.Transform(parentShapeOffset + localPosition, parentOrientation);
        Quaternion orientation = Quaternion.Normalize(parentOrientation * localOrientation);
        Vector3 shapeOffset = VectorOf(source.Shape?.Offset) / UnitsPerBlock;

        Box? box = null;

        if (source.Shape is { Type: "box", Settings.Size: { } size })
        {
            Vector3 dimensions = VectorOf(size);

            if (dimensions.X > 0f && dimensions.Y > 0f && dimensions.Z > 0f)
            {
                Vector3 centre = position + Vector3.Transform(shapeOffset, orientation);
                IReadOnlyDictionary<string, TextureFace> layout = source.Shape.TextureLayout?
                    .ToDictionary(
                        pair => pair.Key,
                        pair => new TextureFace(
                            pair.Value.Offset?.X ?? 0f,
                            pair.Value.Offset?.Y ?? 0f,
                            pair.Value.Mirror?.X ?? false,
                            pair.Value.Mirror?.Y ?? false,
                            pair.Value.Angle),
                        StringComparer.OrdinalIgnoreCase)
                    ?? new Dictionary<string, TextureFace>(StringComparer.OrdinalIgnoreCase);

                box = new Box(centre, dimensions / (UnitsPerBlock * 2f), orientation, layout);
            }
        }

        result.Add(new Node(source.Name ?? string.Empty, position, orientation, box));

        if (source.Children is null)
        {
            return;
        }

        foreach (NodeDocument child in source.Children)
        {
            Flatten(child, position, orientation, shapeOffset, result);
        }
    }

    private static Vector3 VectorOf(VectorDocument? vector) => vector is null
        ? Vector3.Zero
        : new Vector3(vector.X, vector.Y, vector.Z);

    private static Quaternion QuaternionOf(QuaternionDocument? quaternion)
    {
        if (quaternion is null)
        {
            return Quaternion.Identity;
        }

        Quaternion result = new(quaternion.X, quaternion.Y, quaternion.Z, quaternion.W);
        return result.LengthSquared < 1e-8f ? Quaternion.Identity : Quaternion.Normalize(result);
    }

    /// <summary>Jeden uzel modelu se složenou transformací rodičů.</summary>
    public readonly record struct Node(string Name, Vector3 Position, Quaternion Orientation, Box? Box);

    /// <summary>Otočený kvádr modelu. <see cref="HalfSize"/> je v blocích.</summary>
    public readonly record struct Box(
        Vector3 Centre,
        Vector3 HalfSize,
        Quaternion Orientation,
        IReadOnlyDictionary<string, TextureFace> TextureLayout);

    public readonly record struct TextureFace(float X, float Y, bool MirrorX, bool MirrorY, int Angle);

    private static IEnumerable<Vector3> Corners(Box box)
    {
        for (int x = -1; x <= 1; x += 2)
        for (int y = -1; y <= 1; y += 2)
        for (int z = -1; z <= 1; z += 2)
        {
            Vector3 local = new(box.HalfSize.X * x, box.HalfSize.Y * y, box.HalfSize.Z * z);
            yield return box.Centre + Vector3.Transform(local, box.Orientation);
        }
    }

    private static Vector2[] TextureCoordinates(
        TextureFace face, float width, float height, float atlasWidth, float atlasHeight)
    {
        float sizeU = width;
        float sizeV = height;
        float mirrorU = face.MirrorX ? -1f : 1f;
        float mirrorV = face.MirrorY ? -1f : 1f;
        float u0, v0, u1, v1;
        int angle = ((face.Angle % 360) + 360) % 360;

        switch (angle)
        {
            case 90:
                (sizeU, sizeV) = (sizeV, sizeU);
                (mirrorU, mirrorV) = (mirrorV, mirrorU);
                mirrorU *= -1f;
                u0 = face.X;
                v0 = face.Y + (sizeV * mirrorV);
                u1 = face.X + (sizeU * mirrorU);
                v1 = face.Y;
                break;
            case 270:
                (sizeU, sizeV) = (sizeV, sizeU);
                (mirrorU, mirrorV) = (mirrorV, mirrorU);
                mirrorV *= -1f;
                u0 = face.X + (sizeU * mirrorU);
                v0 = face.Y;
                u1 = face.X;
                v1 = face.Y + (sizeV * mirrorV);
                break;
            case 180:
                mirrorU *= -1f;
                mirrorV *= -1f;
                u0 = face.X + (sizeU * mirrorU);
                v0 = face.Y + (sizeV * mirrorV);
                u1 = face.X;
                v1 = face.Y;
                break;
            default:
                u0 = face.X;
                v0 = face.Y;
                u1 = face.X + (sizeU * mirrorU);
                v1 = face.Y + (sizeV * mirrorV);
                break;
        }

        // Maly inset drzi bilinearni vzorek uvnitr vyrezu a nebere sousedni dil atlasu.
        float insetU = 0.125f;
        float insetV = 0.125f;
        u0 += u0 < u1 ? insetU : -insetU;
        u1 += u0 < u1 ? -insetU : insetU;
        v0 += v0 < v1 ? insetV : -insetV;
        v1 += v0 < v1 ? -insetV : insetV;

        Vector2[] corners =
        [
            new(u0 / atlasWidth, v0 / atlasHeight), // top-left
            new(u1 / atlasWidth, v0 / atlasHeight), // top-right
            new(u0 / atlasWidth, v1 / atlasHeight), // bottom-left
            new(u1 / atlasWidth, v1 / atlasHeight), // bottom-right
        ];

        for (int remaining = angle; remaining > 0; remaining -= 90)
        {
            Vector2 first = corners[0];
            corners[0] = corners[2];
            corners[2] = corners[3];
            corners[3] = corners[1];
            corners[1] = first;
        }

        // Geometrie ma poradi bottom-left, bottom-right, top-right, top-left.
        return [corners[2], corners[3], corners[1], corners[0]];
    }

    private static float ShadeForNormal(Vector3 normal)
    {
        Vector3 n = normal.LengthSquared < 1e-8f ? Vector3.UnitY : Vector3.Normalize(normal);
        float top = MathF.Max(0f, n.Y);
        float bottom = MathF.Max(0f, -n.Y);
        float side = 1f - MathF.Abs(n.Y);
        float directional = 0.72f + (0.12f * MathF.Abs(n.Z)) + (0.04f * MathF.Max(0f, n.X));
        return Math.Clamp((top * 1f) + (side * directional) + (bottom * 0.58f), 0.5f, 1f);
    }

    private sealed class Document
    {
        public NodeDocument[]? Nodes { get; set; }
    }

    private sealed class NodeDocument
    {
        public string? Name { get; set; }
        public VectorDocument? Position { get; set; }
        public QuaternionDocument? Orientation { get; set; }
        public ShapeDocument? Shape { get; set; }
        public NodeDocument[]? Children { get; set; }
    }

    private sealed class ShapeDocument
    {
        public string? Type { get; set; }
        public VectorDocument? Offset { get; set; }
        public SettingsDocument? Settings { get; set; }
        public Dictionary<string, TextureFaceDocument>? TextureLayout { get; set; }
    }

    private sealed class SettingsDocument
    {
        public VectorDocument? Size { get; set; }
    }

    private sealed class TextureFaceDocument
    {
        public VectorDocument? Offset { get; set; }
        public BoolVectorDocument? Mirror { get; set; }
        public int Angle { get; set; }
    }

    private sealed class BoolVectorDocument
    {
        public bool X { get; set; }
        public bool Y { get; set; }
    }

    private class VectorDocument
    {
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
    }

    private sealed class QuaternionDocument : VectorDocument
    {
        public float W { get; set; } = 1f;
    }
}

/// <summary>Animace uzlů z formátu <c>.blockyanim</c>.</summary>
public sealed class HytaleBlockAnimation
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    private HytaleBlockAnimation(float duration, bool holdLastKeyframe, IReadOnlyDictionary<string, Keyframe[]> orientations)
    {
        Duration = duration;
        HoldLastKeyframe = holdLastKeyframe;
        Orientations = orientations;
    }

    /// <summary>Délka animace v tikách autora modelu.</summary>
    public float Duration { get; }

    /// <summary>Má animace po dohrání držet poslední polohu?</summary>
    public bool HoldLastKeyframe { get; }

    /// <summary>Keyframy natočení podle jména uzlu.</summary>
    public IReadOnlyDictionary<string, Keyframe[]> Orientations { get; }

    public static HytaleBlockAnimation? TryLoad(string path)
    {
        try
        {
            return Parse(File.ReadAllText(path));
        }
        catch (Exception error) when (error is IOException or JsonException or InvalidDataException)
        {
            Engine.Core.Log.Warn($"Hytale animaci {path} se nepodarilo nacist: {error.Message}");
            return null;
        }
    }

    public static HytaleBlockAnimation Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        AnimationDocument? document = JsonSerializer.Deserialize<AnimationDocument>(json, Options);

        if (document is null || document.Duration <= 0f)
        {
            throw new InvalidDataException("Blocky animace nema platnou delku.");
        }

        var orientations = new Dictionary<string, Keyframe[]>(StringComparer.Ordinal);

        foreach ((string name, NodeAnimationDocument animation) in document.NodeAnimations ?? [])
        {
            Keyframe[] frames = (animation.Orientation ?? [])
                .Select(frame => new Keyframe(frame.Time, QuaternionOf(frame.Delta), frame.InterpolationType ?? "linear"))
                .OrderBy(frame => frame.Time)
                .ToArray();

            if (frames.Length > 0)
            {
                orientations[name] = frames;
            }
        }

        return new HytaleBlockAnimation(document.Duration, document.HoldLastKeyframe, orientations);
    }

    /// <summary>Vrátí plynule interpolované natočení uzlu v daném čase.</summary>
    public Quaternion OrientationAt(string nodeName, float time)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeName);

        if (!Orientations.TryGetValue(nodeName, out Keyframe[]? frames))
        {
            return Quaternion.Identity;
        }

        float sample = HoldLastKeyframe ? Math.Clamp(time, 0f, Duration) : PositiveModulo(time, Duration);

        if (sample <= frames[0].Time)
        {
            return frames[0].Orientation;
        }

        for (int i = 1; i < frames.Length; i++)
        {
            if (sample <= frames[i].Time)
            {
                Keyframe before = frames[i - 1];
                Keyframe after = frames[i];
                float blend = (sample - before.Time) / MathF.Max(1e-6f, after.Time - before.Time);
                return Quaternion.Slerp(before.Orientation, after.Orientation, blend);
            }
        }

        return frames[^1].Orientation;
    }

    private static float PositiveModulo(float value, float modulo)
    {
        float result = value % modulo;
        return result < 0f ? result + modulo : result;
    }

    private static Quaternion QuaternionOf(QuaternionDocument? quaternion)
    {
        if (quaternion is null)
        {
            return Quaternion.Identity;
        }

        Quaternion result = new(quaternion.X, quaternion.Y, quaternion.Z, quaternion.W);
        return result.LengthSquared < 1e-8f ? Quaternion.Identity : Quaternion.Normalize(result);
    }

    /// <summary>Jeden keyframe natočení.</summary>
    public readonly record struct Keyframe(float Time, Quaternion Orientation, string Interpolation);

    private sealed class AnimationDocument
    {
        public float Duration { get; set; }
        public bool HoldLastKeyframe { get; set; }
        public Dictionary<string, NodeAnimationDocument>? NodeAnimations { get; set; }
    }

    private sealed class NodeAnimationDocument
    {
        public OrientationDocument[]? Orientation { get; set; }
    }

    private sealed class OrientationDocument
    {
        public float Time { get; set; }
        public QuaternionDocument? Delta { get; set; }
        public string? InterpolationType { get; set; }
    }

    private sealed class QuaternionDocument
    {
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
        public float W { get; set; } = 1f;
    }
}
