using OpenTK.Mathematics;
using Tesseris.Engine.Core;
using Tesseris.Game.Blocks;

namespace Tesseris.Game.Power;

/// <summary>Barva i elektricky oddeleny kanal povrchoveho kabelu.</summary>
public enum CableColor : byte
{
    Red,
    Blue,
    Yellow,
    Green,
}

/// <summary>Druh koncoveho bodu venkovniho vedeni.</summary>
public enum PowerTerminalKind : byte
{
    Connector,
    Relay,
    Transformer,
    Machine,
}

/// <summary>Presny vysledek pokusu o natazeni venkovniho vodice.</summary>
public enum OverheadAddResult : byte
{
    Added,
    SameEndpoint,
    TooLong,
    Duplicate,
    FirstFull,
    SecondFull,
}

/// <summary>Kabel prichycený k jedne stene pevneho bloku.</summary>
public readonly record struct SurfaceCable(
    Vector3i Support,
    BlockFace Face,
    CableColor Color,
    byte Lane);

/// <summary>Hrana steny v dvojnasobnych svetovych souradnicich, aby zustala presne celociselna.</summary>
public readonly record struct SurfaceEdge(Vector3i A2, Vector3i B2)
{
    public static SurfaceEdge Create(Vector3i a, Vector3i b) => Compare(a, b) <= 0
        ? new SurfaceEdge(a, b)
        : new SurfaceEdge(b, a);

    public Vector3 Centre => new(
        (A2.X + B2.X) * 0.25f,
        (A2.Y + B2.Y) * 0.25f,
        (A2.Z + B2.Z) * 0.25f);

    public Vector3 Direction => Vector3.Normalize(new Vector3(
        B2.X - A2.X,
        B2.Y - A2.Y,
        B2.Z - A2.Z));

    private static int Compare(Vector3i a, Vector3i b)
    {
        int x = a.X.CompareTo(b.X);
        if (x != 0) return x;
        int y = a.Y.CompareTo(b.Y);
        return y != 0 ? y : a.Z.CompareTo(b.Z);
    }
}

/// <summary>Jeden venkovni vodic mezi dvema svorkami.</summary>
public readonly record struct OverheadWire(Vector3i A, Vector3i B)
{
    public bool Touches(Vector3i block) => A == block || B == block;
}

/// <summary>
/// Trvala data elektricke site. Geometrie ani simulace sem nepatri: ukladaji se pouze uzly a
/// hrany a vse odvoditelne se po uprave znovu sestavi.
/// </summary>
public sealed class PowerNetwork
{
    public const string FileName = "kabely.dat";
    public const float MaxOverheadLength = 48f;
    public const int MaxLanesPerFace = 4;

    private const uint Magic = 0x31525750; // PWR1

    private readonly HashSet<SurfaceCable> _surface = [];
    private readonly List<OverheadWire> _overhead = [];

    public IReadOnlyCollection<SurfaceCable> Surface => _surface;
    public IReadOnlyList<OverheadWire> Overhead => _overhead;
    public int Revision { get; private set; }

    public static byte LaneOf(CableColor color) => (byte)color;

    public bool ToggleSurface(Vector3i support, BlockFace face, CableColor color)
    {
        var cable = new SurfaceCable(support, face, color, LaneOf(color));

        if (_surface.Remove(cable))
        {
            Revision++;
            return false;
        }

        _surface.Add(cable);
        Revision++;
        return true;
    }

    /// <summary>Polozi novy kabel, ale existujici nikdy neodebere.</summary>
    public bool AddSurface(Vector3i support, BlockFace face, CableColor color)
    {
        var cable = new SurfaceCable(support, face, color, LaneOf(color));
        if (!_surface.Add(cable))
        {
            return false;
        }

        Revision++;
        return true;
    }

    /// <summary>Odebere a vrati presne kabely, ktere na zasazene plose skutecne byly.</summary>
    public SurfaceCable[] TakeSurface(Vector3i support, BlockFace face, CableColor? color = null)
    {
        SurfaceCable[] removed = [.. _surface.Where(cable =>
            cable.Support == support && cable.Face == face
            && (color is null || cable.Color == color.Value))];

        if (removed.Length == 0)
        {
            return [];
        }

        _surface.ExceptWith(removed);
        Revision++;
        return removed;
    }

    /// <summary>Odebere a vrati kabely ze vsech stran niceneho nosneho bloku.</summary>
    public SurfaceCable[] TakeSurfaceAtBlock(Vector3i support)
    {
        SurfaceCable[] removed = [.. _surface.Where(cable => cable.Support == support)];
        if (removed.Length == 0)
        {
            return [];
        }

        _surface.ExceptWith(removed);
        Revision++;
        return removed;
    }

    public bool RemoveSurface(Vector3i support, BlockFace face, CableColor? color = null)
        => TakeSurface(support, face, color).Length > 0;

    public bool TryAddOverhead(
        Vector3i a,
        Vector3i b,
        PowerTerminalKind kindA,
        PowerTerminalKind kindB) =>
        AddOverhead(a, b, kindA, kindB) == OverheadAddResult.Added;

    public OverheadAddResult AddOverhead(
        Vector3i a,
        Vector3i b,
        PowerTerminalKind kindA,
        PowerTerminalKind kindB)
    {
        OverheadAddResult validation = ValidateOverhead(a, b, kindA, kindB);
        if (validation != OverheadAddResult.Added)
        {
            return validation;
        }

        _overhead.Add(new OverheadWire(a, b));
        Revision++;
        return OverheadAddResult.Added;
    }

    public OverheadAddResult ValidateOverhead(
        Vector3i a,
        Vector3i b,
        PowerTerminalKind kindA,
        PowerTerminalKind kindB)
    {
        if (a == b)
        {
            return OverheadAddResult.SameEndpoint;
        }

        if (Distance(a, b) > MaxOverheadLength)
        {
            return OverheadAddResult.TooLong;
        }

        if (_overhead.Any(wire =>
            (wire.A == a && wire.B == b) || (wire.A == b && wire.B == a)))
        {
            return OverheadAddResult.Duplicate;
        }

        if (!HasFreePort(a, kindA))
        {
            return OverheadAddResult.FirstFull;
        }

        return HasFreePort(b, kindB)
            ? OverheadAddResult.Added
            : OverheadAddResult.SecondFull;
    }

    public bool RemoveOverheadAt(Vector3i block)
        => TakeOverheadAt(block).Length > 0;

    /// <summary>Odebere a vrati vsechny venkovni vodice pripojene k zasazene svorce.</summary>
    public OverheadWire[] TakeOverheadAt(Vector3i block)
    {
        OverheadWire[] removed = [.. _overhead.Where(wire => wire.Touches(block))];
        if (removed.Length == 0)
        {
            return [];
        }

        _overhead.RemoveAll(wire => wire.Touches(block));
        Revision++;
        return removed;
    }

    public bool RemoveOverhead(Vector3i a, Vector3i b)
    {
        int removed = _overhead.RemoveAll(wire =>
            (wire.A == a && wire.B == b) || (wire.A == b && wire.B == a));
        if (removed == 0)
        {
            return false;
        }

        Revision++;
        return true;
    }

    public void RemoveBlock(Vector3i block)
    {
        TakeSurfaceAtBlock(block);
        TakeOverheadAt(block);
    }

    public int Degree(Vector3i block) => _overhead.Count(wire => wire.Touches(block));

    public bool HasFreePort(Vector3i block, PowerTerminalKind kind) =>
        Degree(block) < CapacityOf(kind);

    public static int CapacityOf(PowerTerminalKind kind) => kind switch
    {
        PowerTerminalKind.Connector => 2,
        PowerTerminalKind.Relay => 6,
        PowerTerminalKind.Transformer => 2,
        PowerTerminalKind.Machine => 1,
        _ => 0,
    };

    public bool HasSurface(Vector3i support, BlockFace face, CableColor color) =>
        _surface.Contains(new SurfaceCable(support, face, color, LaneOf(color)));

    public bool IsSurfaceConnected(SurfaceCable a, SurfaceCable b)
    {
        if (a == b || a.Color != b.Color || a.Lane != b.Lane)
        {
            return false;
        }
        return TrySharedEdge(a, b, out _);
    }

    public static bool TrySharedEdge(SurfaceCable a, SurfaceCable b, out SurfaceEdge shared)
    {
        foreach (SurfaceEdge first in EdgesOf(a))
        {
            foreach (SurfaceEdge second in EdgesOf(b))
            {
                if (first == second)
                {
                    shared = first;
                    return true;
                }
            }
        }

        shared = default;
        return false;
    }

    public static SurfaceEdge[] EdgesOf(SurfaceCable cable)
    {
        Vector3i normal = FaceNormal(cable.Face);
        (Vector3i tangentA, Vector3i tangentB) = FaceTangents(cable.Face);
        Vector3i centre2 = (cable.Support * 2) + Vector3i.One + normal;
        Vector3i p00 = centre2 - tangentA - tangentB;
        Vector3i p01 = centre2 - tangentA + tangentB;
        Vector3i p10 = centre2 + tangentA - tangentB;
        Vector3i p11 = centre2 + tangentA + tangentB;

        return
        [
            SurfaceEdge.Create(p00, p01),
            SurfaceEdge.Create(p01, p11),
            SurfaceEdge.Create(p11, p10),
            SurfaceEdge.Create(p10, p00),
        ];
    }

    public void Save(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            string fullPath = Path.GetFullPath(path);
            string? directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string temporary = fullPath + ".tmp";
            using (var stream = File.Create(temporary))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(Magic);
                writer.Write(_surface.Count);
                foreach (SurfaceCable cable in _surface.OrderBy(c => c.Support.X)
                             .ThenBy(c => c.Support.Y).ThenBy(c => c.Support.Z)
                             .ThenBy(c => c.Face).ThenBy(c => c.Color))
                {
                    WriteVector(writer, cable.Support);
                    writer.Write((byte)cable.Face);
                    writer.Write((byte)cable.Color);
                    writer.Write(cable.Lane);
                }

                writer.Write(_overhead.Count);
                foreach (OverheadWire wire in _overhead)
                {
                    WriteVector(writer, wire.A);
                    WriteVector(writer, wire.B);
                }
            }

            File.Move(temporary, fullPath, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Elektrickou sit se nepodarilo ulozit: {error.Message}");
        }
    }

    public bool Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream);
            if (reader.ReadUInt32() != Magic)
            {
                return false;
            }

            var surface = new HashSet<SurfaceCable>();
            int surfaceCount = CheckedCount(reader.ReadInt32());
            for (int i = 0; i < surfaceCount; i++)
            {
                Vector3i support = ReadVector(reader);
                var face = (BlockFace)reader.ReadByte();
                var color = (CableColor)reader.ReadByte();
                byte lane = reader.ReadByte();

                if (Enum.IsDefined(face) && Enum.IsDefined(color) && lane < MaxLanesPerFace)
                {
                    surface.Add(new SurfaceCable(support, face, color, lane));
                }
            }

            var overhead = new List<OverheadWire>();
            int overheadCount = CheckedCount(reader.ReadInt32());
            for (int i = 0; i < overheadCount; i++)
            {
                Vector3i a = ReadVector(reader);
                Vector3i b = ReadVector(reader);
                if (a != b && Distance(a, b) <= MaxOverheadLength)
                {
                    overhead.Add(new OverheadWire(a, b));
                }
            }

            _surface.Clear();
            _surface.UnionWith(surface);
            _overhead.Clear();
            _overhead.AddRange(overhead);
            Revision++;
            return true;
        }
        catch (Exception error) when (error is IOException or EndOfStreamException or InvalidDataException)
        {
            Log.Warn($"Elektrickou sit se nepodarilo nacist: {error.Message}");
            return false;
        }
    }

    public static Vector3i FaceNormal(BlockFace face) => face switch
    {
        BlockFace.NegX => -Vector3i.UnitX,
        BlockFace.PosX => Vector3i.UnitX,
        BlockFace.NegY => -Vector3i.UnitY,
        BlockFace.PosY => Vector3i.UnitY,
        BlockFace.NegZ => -Vector3i.UnitZ,
        BlockFace.PosZ => Vector3i.UnitZ,
        _ => Vector3i.Zero,
    };

    private static (Vector3i A, Vector3i B) FaceTangents(BlockFace face) => face switch
    {
        BlockFace.NegX or BlockFace.PosX => (Vector3i.UnitY, Vector3i.UnitZ),
        BlockFace.NegY or BlockFace.PosY => (Vector3i.UnitX, Vector3i.UnitZ),
        _ => (Vector3i.UnitX, Vector3i.UnitY),
    };

    private static float Distance(Vector3i a, Vector3i b) =>
        new Vector3(a.X - b.X, a.Y - b.Y, a.Z - b.Z).Length;

    private static int CheckedCount(int value) =>
        value is >= 0 and <= 1_000_000 ? value : throw new InvalidDataException("Neplatny pocet kabelu.");

    private static void WriteVector(BinaryWriter writer, Vector3i value)
    {
        writer.Write(value.X);
        writer.Write(value.Y);
        writer.Write(value.Z);
    }

    private static Vector3i ReadVector(BinaryReader reader) =>
        new(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());
}
