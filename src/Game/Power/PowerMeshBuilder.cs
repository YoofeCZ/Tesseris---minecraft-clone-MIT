using OpenTK.Mathematics;
using Tesseris.Game.Blocks;
using Tesseris.Game.World;

namespace Tesseris.Game.Power;

/// <summary>Stavi pevnou geometrii kabelu pouze tehdy, kdyz se sit zmeni.</summary>
public static class PowerMeshBuilder
{
    private const float SurfaceOffset = 0.512f;
    private const float CableHalfWidth = 0.035f;
    private const float CableHalfHeight = 0.025f;

    private readonly record struct JunctionSegment(Vector3 From, Vector3 To, CableColor Color);

    public static void Build(
        PowerNetwork network,
        MeshBuffer mesh,
        IReadOnlyList<int> colorLayers,
        int wireLayer,
        int junctionLayer)
    {
        ArgumentNullException.ThrowIfNull(network);
        ArgumentNullException.ThrowIfNull(mesh);
        if (colorLayers.Count < PowerNetwork.MaxLanesPerFace)
        {
            throw new ArgumentException("Chybi vrstvy barevnych kabelu.", nameof(colorLayers));
        }

        mesh.Clear();
        BuildSurface(network, mesh, colorLayers, junctionLayer);
        BuildOverhead(network, mesh, wireLayer);
    }

    public static Vector3 TerminalPoint(Vector3i block) =>
        new(block.X + 0.5f, block.Y + 0.86f, block.Z + 0.5f);

    public static Vector3 SagPoint(Vector3 a, Vector3 b, float t)
    {
        float distance = (b - a).Length;
        float sag = Math.Clamp(distance * 0.055f, 0.35f, 2.7f);
        return Vector3.Lerp(a, b, t) - (Vector3.UnitY * (4f * t * (1f - t) * sag));
    }

    private static void BuildSurface(
        PowerNetwork network,
        MeshBuffer mesh,
        IReadOnlyList<int> layers,
        int junctionLayer)
    {
        var adjacency = network.Surface.ToDictionary(cable => cable, _ => new List<SurfaceCable>());
        var byEdge = new Dictionary<SurfaceEdge, List<SurfaceCable>>();
        var connections = new List<(SurfaceCable First, SurfaceCable Second, SurfaceEdge Edge)>();

        foreach (SurfaceCable cable in network.Surface)
        {
            foreach (SurfaceEdge edge in PowerNetwork.EdgesOf(cable))
            {
                if (!byEdge.TryGetValue(edge, out List<SurfaceCable>? bucket))
                {
                    bucket = [];
                    byEdge.Add(edge, bucket);
                }

                bucket.Add(cable);
            }
        }

        foreach ((SurfaceEdge edge, List<SurfaceCable> bucket) in byEdge)
        {
            for (int firstIndex = 0; firstIndex < bucket.Count; firstIndex++)
            {
                SurfaceCable first = bucket[firstIndex];
                for (int secondIndex = firstIndex + 1; secondIndex < bucket.Count; secondIndex++)
                {
                    SurfaceCable second = bucket[secondIndex];
                    if (!network.IsSurfaceConnected(first, second))
                    {
                        continue;
                    }

                    adjacency[first].Add(second);
                    adjacency[second].Add(first);
                    connections.Add((first, second, edge));
                }
            }

        }

        foreach ((SurfaceCable first, SurfaceCable second, SurfaceEdge edge) in connections)
        {
            AddSurfaceConnection(mesh, first, second, edge, layers[(int)first.Color]);
        }

        foreach (SurfaceCable cable in network.Surface)
        {
            Vector3 centre = SurfaceCentre(cable);
            Vector3 normal = ToVector(PowerNetwork.FaceNormal(cable.Face));
            (Vector3 tangentA, Vector3 tangentB) = Tangents(cable.Face);

            if (adjacency[cable].Count == 0)
            {
                AddSurfaceBox(
                    mesh, centre, normal, tangentA, tangentB,
                    0.12f, CableHalfWidth, layers[(int)cable.Color]);
            }
        }

        foreach (IGrouping<(Vector3i Support, BlockFace Face), SurfaceCable> group in
                 network.Surface.GroupBy(cable => (cable.Support, cable.Face)))
        {
            SurfaceCable[] cables = [.. group];
            if (cables.Length < 2 || !NeedsFaceJunction(cables, adjacency))
            {
                continue;
            }

            Vector3 normal = ToVector(PowerNetwork.FaceNormal(group.Key.Face));
            (Vector3 tangentA, Vector3 tangentB) = Tangents(group.Key.Face);
            Vector3 centre = new(
                group.Key.Support.X + 0.5f,
                group.Key.Support.Y + 0.5f,
                group.Key.Support.Z + 0.5f);
            centre += normal * (SurfaceOffset + 0.052f);
            AddSurfaceBox(mesh, centre, normal, tangentA, tangentB, 0.245f, 0.245f, junctionLayer);
        }

    }

    private static void AddSurfaceConnection(
        MeshBuffer mesh,
        SurfaceCable first,
        SurfaceCable second,
        SurfaceEdge edge,
        int layer)
    {
        Vector3 firstCentre = SurfaceCentre(first);
        Vector3 secondCentre = SurfaceCentre(second);

        if (first.Face == second.Face)
        {
            Vector3 normal = ToVector(PowerNetwork.FaceNormal(first.Face));
            (Vector3 tangentA, Vector3 tangentB) = Tangents(first.Face);
            Vector3 midpoint = (firstCentre + secondCentre) * 0.5f;
            Vector3 delta = secondCentre - firstCentre;
            float alongA = MathF.Abs(Vector3.Dot(delta, tangentA)) * 0.5f + CableHalfWidth;
            float alongB = MathF.Abs(Vector3.Dot(delta, tangentB)) * 0.5f + CableHalfWidth;
            AddSurfaceBox(mesh, midpoint, normal, tangentA, tangentB, alongA, alongB, layer);
            return;
        }

        Vector3 corner = SurfaceCorner(first, second, edge);
        AddTubeSegment(mesh, firstCentre, corner, CableHalfWidth, layer);
        AddTubeSegment(mesh, corner, secondCentre, CableHalfWidth, layer);
    }

    private static Vector3 SurfaceCorner(SurfaceCable first, SurfaceCable second, SurfaceEdge edge)
    {
        Vector3 normalA = ToVector(PowerNetwork.FaceNormal(first.Face));
        Vector3 normalB = ToVector(PowerNetwork.FaceNormal(second.Face));
        return edge.Centre
            + ((normalA + normalB) * ((SurfaceOffset - 0.5f) + CableHalfHeight))
            + (edge.Direction * LaneOffset(first.Lane));
    }

    private static bool NeedsFaceJunction(
        IReadOnlyList<SurfaceCable> cables,
        IReadOnlyDictionary<SurfaceCable, List<SurfaceCable>> adjacency)
    {
        if (cables.Select(cable => cable.Color).Distinct().Count() < 2)
        {
            return false;
        }

        var segments = new List<JunctionSegment>();
        foreach (SurfaceCable cable in cables)
        {
            Vector3 from = SurfaceCentre(cable);
            foreach (SurfaceCable neighbour in adjacency[cable])
            {
                Vector3 to;
                if (neighbour.Face == cable.Face)
                {
                    to = (from + SurfaceCentre(neighbour)) * 0.5f;
                }
                else if (PowerNetwork.TrySharedEdge(cable, neighbour, out SurfaceEdge edge))
                {
                    to = SurfaceCorner(cable, neighbour, edge);
                }
                else
                {
                    continue;
                }

                segments.Add(new JunctionSegment(from, to, cable.Color));
            }
        }

        float intersectionDistanceSquared = MathF.Pow(CableHalfWidth * 2.05f, 2f);
        for (int first = 0; first < segments.Count; first++)
        {
            JunctionSegment a = segments[first];
            Vector3 directionA = a.To - a.From;
            for (int second = first + 1; second < segments.Count; second++)
            {
                JunctionSegment b = segments[second];
                if (a.Color == b.Color)
                {
                    continue;
                }

                Vector3 directionB = b.To - b.From;
                float parallelScale = directionA.LengthSquared * directionB.LengthSquared;
                if (parallelScale <= 1e-8f
                    || Vector3.Cross(directionA, directionB).LengthSquared <= parallelScale * 1e-6f)
                {
                    continue;
                }

                if (SegmentDistanceSquared(a.From, a.To, b.From, b.To) <= intersectionDistanceSquared)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static float SegmentDistanceSquared(Vector3 p0, Vector3 p1, Vector3 q0, Vector3 q1)
    {
        Vector3 u = p1 - p0;
        Vector3 v = q1 - q0;
        Vector3 w = p0 - q0;
        float a = Vector3.Dot(u, u);
        float b = Vector3.Dot(u, v);
        float c = Vector3.Dot(v, v);
        float d = Vector3.Dot(u, w);
        float e = Vector3.Dot(v, w);
        float denominator = (a * c) - (b * b);
        float numeratorS;
        float denominatorS = denominator;
        float numeratorT;
        float denominatorT = denominator;

        if (denominator < 1e-8f)
        {
            numeratorS = 0f;
            denominatorS = 1f;
            numeratorT = e;
            denominatorT = c;
        }
        else
        {
            numeratorS = (b * e) - (c * d);
            numeratorT = (a * e) - (b * d);
            if (numeratorS < 0f)
            {
                numeratorS = 0f;
                numeratorT = e;
                denominatorT = c;
            }
            else if (numeratorS > denominatorS)
            {
                numeratorS = denominatorS;
                numeratorT = e + b;
                denominatorT = c;
            }
        }

        if (numeratorT < 0f)
        {
            numeratorT = 0f;
            if (-d < 0f)
            {
                numeratorS = 0f;
            }
            else if (-d > a)
            {
                numeratorS = denominatorS;
            }
            else
            {
                numeratorS = -d;
                denominatorS = a;
            }
        }
        else if (numeratorT > denominatorT)
        {
            numeratorT = denominatorT;
            if ((-d + b) < 0f)
            {
                numeratorS = 0f;
            }
            else if ((-d + b) > a)
            {
                numeratorS = denominatorS;
            }
            else
            {
                numeratorS = -d + b;
                denominatorS = a;
            }
        }

        float s = MathF.Abs(numeratorS) < 1e-8f ? 0f : numeratorS / denominatorS;
        float t = MathF.Abs(numeratorT) < 1e-8f ? 0f : numeratorT / denominatorT;
        Vector3 separation = w + (s * u) - (t * v);
        return separation.LengthSquared;
    }

    private static void BuildOverhead(PowerNetwork network, MeshBuffer mesh, int layer)
    {
        foreach (OverheadWire wire in network.Overhead)
        {
            Vector3 a = TerminalPoint(wire.A);
            Vector3 b = TerminalPoint(wire.B);
            int steps = Math.Clamp((int)((b - a).Length * 1.5f), 8, 48);
            Vector3 previous = a;

            for (int step = 1; step <= steps; step++)
            {
                Vector3 next = SagPoint(a, b, step / (float)steps);
                AddTubeSegment(mesh, previous, next, 0.028f, layer);
                previous = next;
            }
        }
    }

    private static Vector3 SurfaceCentre(SurfaceCable cable)
    {
        Vector3 normal = ToVector(PowerNetwork.FaceNormal(cable.Face));
        (Vector3 tangentA, Vector3 tangentB) = Tangents(cable.Face);
        float laneOffset = LaneOffset(cable.Lane);
        return new Vector3(cable.Support.X + 0.5f, cable.Support.Y + 0.5f, cable.Support.Z + 0.5f)
            + (normal * SurfaceOffset)
            + ((tangentA + tangentB) * laneOffset);
    }

    private static float LaneOffset(byte lane) => (lane - 1.5f) * 0.105f;

    private static void AddSurfaceBox(
        MeshBuffer mesh,
        Vector3 centre,
        Vector3 normal,
        Vector3 tangentA,
        Vector3 tangentB,
        float halfA,
        float halfB,
        int layer)
    {
        if (Vector3.Dot(Vector3.Cross(tangentA, tangentB), normal) < 0f)
        {
            tangentB = -tangentB;
        }

        Vector3 a = tangentA * halfA;
        Vector3 b = tangentB * halfB;
        Vector3 n = normal * CableHalfHeight;
        AddBox(mesh, centre, a, b, n, layer);
    }

    private static void AddTubeSegment(MeshBuffer mesh, Vector3 from, Vector3 to, float radius, int layer)
    {
        Vector3 direction = Vector3.Normalize(to - from);
        Vector3 reference = MathF.Abs(Vector3.Dot(direction, Vector3.UnitY)) > 0.9f
            ? Vector3.UnitX
            : Vector3.UnitY;
        Vector3 side = Vector3.Normalize(Vector3.Cross(direction, reference)) * radius;
        Vector3 up = Vector3.Normalize(Vector3.Cross(direction, side)) * radius;
        AddBox(mesh, (from + to) * 0.5f, (to - from) * 0.5f, side, up, layer);
    }

    private static void AddBox(MeshBuffer mesh, Vector3 centre, Vector3 a, Vector3 b, Vector3 c, int layer)
    {
        Vector3 p000 = centre - a - b - c;
        Vector3 p001 = centre - a - b + c;
        Vector3 p010 = centre - a + b - c;
        Vector3 p011 = centre - a + b + c;
        Vector3 p100 = centre + a - b - c;
        Vector3 p101 = centre + a - b + c;
        Vector3 p110 = centre + a + b - c;
        Vector3 p111 = centre + a + b + c;
        Vector2 uv0 = Vector2.Zero;
        Vector2 uv1 = Vector2.UnitX;
        Vector2 uv2 = Vector2.One;
        Vector2 uv3 = Vector2.UnitY;

        AddQuad(mesh, p100, p110, p111, p101, uv0, uv1, uv2, uv3, layer);
        AddQuad(mesh, p000, p001, p011, p010, uv0, uv1, uv2, uv3, layer);
        AddQuad(mesh, p010, p011, p111, p110, uv0, uv1, uv2, uv3, layer);
        AddQuad(mesh, p000, p100, p101, p001, uv0, uv1, uv2, uv3, layer);
        AddQuad(mesh, p001, p101, p111, p011, uv0, uv1, uv2, uv3, layer);
        AddQuad(mesh, p000, p010, p110, p100, uv0, uv1, uv2, uv3, layer);
    }

    private static void AddQuad(
        MeshBuffer mesh,
        Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3,
        Vector2 uv0, Vector2 uv1, Vector2 uv2, Vector2 uv3,
        int layer) =>
        mesh.AddQuad(p0, p1, p2, p3, uv0, uv1, uv2, uv3, layer, 1f, 1f, 1f, 1f, false);

    private static (Vector3 A, Vector3 B) Tangents(BlockFace face) => face switch
    {
        BlockFace.NegX or BlockFace.PosX => (Vector3.UnitZ, Vector3.UnitY),
        BlockFace.NegY or BlockFace.PosY => (Vector3.UnitX, Vector3.UnitZ),
        _ => (Vector3.UnitX, Vector3.UnitY),
    };

    private static Vector3 ToVector(Vector3i value) => new(value.X, value.Y, value.Z);
}
