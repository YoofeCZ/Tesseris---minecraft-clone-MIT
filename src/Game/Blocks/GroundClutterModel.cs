using System.Text.Json;
using OpenTK.Mathematics;
using Tesseris.Engine.Rendering;

namespace Tesseris.Game.Blocks;

/// <summary>Nízký Blockbench model použitý přímo jako blok ležící na zemi.</summary>
/// <remarks>
/// Formát je Minecraft Java Block/Item: souřadnice geometrie i UV jsou v šestnáctinách.
/// Nález používá texturu ze své definice bloku, protože Blockbench může v exportu
/// ponechat obecné jméno typu <c>block/texture</c>.
/// </remarks>
public sealed class GroundClutterModel
{
    public readonly record struct Face(
        Vector3 P0, Vector3 P1, Vector3 P2, Vector3 P3,
        Vector2 U0, Vector2 U1, Vector2 U2, Vector2 U3,
        float Shade);

    public readonly record struct Box(Vector3 Min, Vector3 Max);

    private GroundClutterModel(Face[] faces, Box[] boxes)
    {
        Faces = faces;
        Boxes = boxes;
    }

    public Face[] Faces { get; }

    /// <summary>Stejné kvádry, jaké se kreslí; používá je přesné zaměřování.</summary>
    public Box[] Boxes { get; }

    public static GroundClutterModel Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));

        if (!document.RootElement.TryGetProperty("elements", out JsonElement elements)
            || elements.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"Zemní model {path} nemá pole 'elements'.");
        }

        List<Face> faces = [];
        List<Box> boxes = [];

        foreach (JsonElement element in elements.EnumerateArray())
        {
            Vector3 from = ReadVector(element, "from", path) / 16f;
            Vector3 to = ReadVector(element, "to", path) / 16f;
            Vector3 min = Vector3.ComponentMin(from, to);
            Vector3 max = Vector3.ComponentMax(from, to);

            if (element.TryGetProperty("rotation", out JsonElement rotation)
                && rotation.TryGetProperty("angle", out JsonElement angle)
                && MathF.Abs(angle.GetSingle()) > 0.001f)
            {
                throw new InvalidDataException(
                    $"Zemní model {path} používá natočený element; podporované jsou osové kvádry.");
            }

            boxes.Add(new Box(min, max));

            if (!element.TryGetProperty("faces", out JsonElement elementFaces))
            {
                continue;
            }

            var c = new Vector3[8];
            for (int i = 0; i < c.Length; i++)
            {
                c[i] = new Vector3(
                    (i & 1) == 0 ? min.X : max.X,
                    (i & 2) == 0 ? min.Y : max.Y,
                    (i & 4) == 0 ? min.Z : max.Z);
            }

            Add("north", [c[1], c[0], c[2], c[3]], axis: 2, positive: false);
            Add("south", [c[4], c[5], c[7], c[6]], axis: 2, positive: true);
            Add("west", [c[0], c[4], c[6], c[2]], axis: 0, positive: false);
            Add("east", [c[5], c[1], c[3], c[7]], axis: 0, positive: true);
            Add("up", [c[6], c[7], c[3], c[2]], axis: 1, positive: true);
            Add("down", [c[0], c[1], c[5], c[4]], axis: 1, positive: false);

            void Add(string name, Vector3[] points, int axis, bool positive)
            {
                if (!elementFaces.TryGetProperty(name, out JsonElement face)
                    || !face.TryGetProperty("uv", out JsonElement uv)
                    || uv.ValueKind != JsonValueKind.Array
                    || uv.GetArrayLength() != 4)
                {
                    return;
                }

                float[] values = [.. uv.EnumerateArray().Select(value => value.GetSingle() / 16f)];
                float u0 = MathF.Min(values[0], values[2]);
                float u1 = MathF.Max(values[0], values[2]);
                float v0 = MathF.Min(values[1], values[3]);
                float v1 = MathF.Max(values[1], values[3]);

                faces.Add(new Face(
                    points[0], points[1], points[2], points[3],
                    new Vector2(u0, v1), new Vector2(u1, v1),
                    new Vector2(u1, v0), new Vector2(u0, v0),
                    FaceShading.ForAxis(axis, positive)));
            }
        }

        if (boxes.Count == 0 || faces.Count == 0)
        {
            throw new InvalidDataException($"Zemní model {path} neobsahuje použitelný kvádr.");
        }

        return new GroundClutterModel([.. faces], [.. boxes]);
    }

    private static Vector3 ReadVector(JsonElement element, string name, string path)
    {
        if (!element.TryGetProperty(name, out JsonElement value)
            || value.ValueKind != JsonValueKind.Array
            || value.GetArrayLength() != 3)
        {
            throw new InvalidDataException($"Element zemního modelu {path} nemá platné '{name}'.");
        }

        float[] parts = [.. value.EnumerateArray().Select(part => part.GetSingle())];
        return new Vector3(parts[0], parts[1], parts[2]);
    }
}
