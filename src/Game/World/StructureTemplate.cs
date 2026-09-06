using System.Text.Json;
using System.Text.Json.Serialization;
using OpenTK.Mathematics;
using Tesseris.Game.Blocks;

namespace Tesseris.Game.World;

public sealed class StructureTemplate
{
    public int FormatVersion { get; set; } = 1;
    public string Name { get; set; } = string.Empty;
    public int SizeX { get; set; }
    public int SizeY { get; set; }
    public int SizeZ { get; set; }
    public int AnchorX { get; set; }
    public int AnchorY { get; set; }
    public int AnchorZ { get; set; }
    public List<StructureCell> Blocks { get; set; } = [];
    public List<StructureLootMarker> Loot { get; set; } = [];
    public StructureSpawnRules Spawn { get; set; } = new();

    public static StructureTemplate Capture(
        VoxelWorld world, Vector3i first, Vector3i second, string name)
    {
        ArgumentNullException.ThrowIfNull(world);
        Vector3i min = Vector3i.ComponentMin(first, second);
        Vector3i max = Vector3i.ComponentMax(first, second);
        Vector3i size = max - min + Vector3i.One;
        if (size.X > 64 || size.Y > 64 || size.Z > 64)
        {
            throw new InvalidOperationException("Struktura může mít nejvýše 64×64×64 bloků.");
        }

        var result = new StructureTemplate
        {
            Name = StructureTemplateStore.ValidateName(name),
            SizeX = size.X,
            SizeY = size.Y,
            SizeZ = size.Z,
            AnchorX = size.X / 2,
            AnchorY = 0,
            AnchorZ = size.Z / 2,
        };

        for (int y = 0; y < size.Y; y++)
        for (int z = 0; z < size.Z; z++)
        for (int x = 0; x < size.X; x++)
        {
            int wx = min.X + x;
            int wy = min.Y + y;
            int wz = min.Z + z;
            ushort block = world.GetBlock(wx, wy, wz);
            if (block == BlockRegistry.Air) continue;
            string id = world.Registry.Definition(block).Id;
            result.Blocks.Add(new StructureCell(x, y, z, id, world.GetPieces(wx, wy, wz)));
            if (id is "tesseris:chest" or "tesseris:loot_chest")
            {
                result.Loot.Add(new StructureLootMarker(x, y, z, "default", Random: true));
            }
        }

        return result;
    }

    public int Place(VoxelWorld world, Vector3i origin, int rotation = 0)
    {
        ArgumentNullException.ThrowIfNull(world);
        int count = 0;
        foreach (StructureCell cell in Blocks)
        {
            if (!world.Registry.TryIndexOf(cell.BlockId, out ushort block)) continue;
            (int x, int z) = Rotate(cell.X - AnchorX, cell.Z - AnchorZ, rotation);
            int wx = origin.X + x;
            int wy = origin.Y + cell.Y - AnchorY;
            int wz = origin.Z + z;
            world.SetBlock(wx, wy, wz, block);
            world.SetPieces(wx, wy, wz,
                RotateState(world.Registry.ShapeOf(block), cell.Pieces, rotation));
            count++;
        }
        return count;
    }

    internal void Place(
        Chunk chunk, Vector3i chunkPosition, BlockRegistry registry,
        int originX, int originY, int originZ, int rotation)
    {
        foreach (StructureCell cell in Blocks)
        {
            if (!registry.TryIndexOf(cell.BlockId, out ushort block)) continue;
            (int x, int z) = Rotate(cell.X - AnchorX, cell.Z - AnchorZ, rotation);
            int wx = originX + x;
            int wy = originY + cell.Y - AnchorY;
            int wz = originZ + z;
            int lx = wx - (chunkPosition.X * Chunk.Size);
            int ly = wy - (chunkPosition.Y * Chunk.Size);
            int lz = wz - (chunkPosition.Z * Chunk.Size);
            if ((uint)lx >= Chunk.Size || (uint)ly >= Chunk.Size || (uint)lz >= Chunk.Size) continue;
            chunk.SetBlock(lx, ly, lz, block);
            chunk.SetPieces(lx, ly, lz, RotateState(registry.ShapeOf(block), cell.Pieces, rotation));
        }
    }

    private static (int X, int Z) Rotate(int x, int z, int rotation) => (rotation & 3) switch
    {
        1 => (z, -x),
        2 => (-x, -z),
        3 => (-z, x),
        _ => (x, z),
    };

    private static byte RotateState(BlockShape shape, byte state, int rotation)
    {
        int turns = rotation & 3;
        if (turns == 0) return state;
        if (shape == BlockShape.Stairs)
        {
            return PieceMask.StairState(
                (PieceMask.StairFacing(state) + turns) & 3,
                PieceMask.StairUpsideDown(state), PieceMask.StairCorner(state));
        }
        if (shape == BlockShape.Door)
        {
            return PieceMask.DoorState(
                (PieceMask.DoorFacing(state) + turns) & 3,
                PieceMask.DoorHingeRight(state), PieceMask.DoorIsOpen(state));
        }
        if (shape == BlockShape.Ladder)
        {
            return PieceMask.LadderState((PieceMask.LadderFacing(state) + turns) & 3);
        }
        if (shape == BlockShape.Trapdoor && (turns & 1) != 0)
        {
            byte rotated = PieceMask.TrapdoorClosed(
                PieceMask.TrapdoorIsTop(state), !PieceMask.TrapdoorIsAlongZ(state));
            return PieceMask.TrapdoorIsOpen(state) ? PieceMask.ToggleTrapdoor(rotated) : rotated;
        }
        return state;
    }
}

public readonly record struct StructureCell(
    int X, int Y, int Z, string BlockId, byte Pieces);

public readonly record struct StructureLootMarker(
    int X, int Y, int Z, string Table, bool Random);

public sealed class StructureSpawnRules
{
    public bool Enabled { get; set; }
    public int ChancePercent { get; set; } = 15;
    public int SpacingChunks { get; set; } = 12;
    public int MinY { get; set; } = 0;
    public int MaxY { get; set; } = TerrainGenerator.WorldHeight - 1;
    public int SurfaceOffset { get; set; } = 1;
    public string[] Biomes { get; set; } = [];
}

public static class StructureTemplateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string DefaultDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Tesseris", "structures");

    public static string ValidateName(string name)
    {
        string value = (name ?? string.Empty).Trim();
        if (value.Length is < 1 or > 48
            || value.Any(character => !(char.IsLetterOrDigit(character)
                || character is '_' or '-')))
        {
            throw new InvalidDataException(
                "Název musí mít 1–48 znaků a smí obsahovat jen písmena, čísla, _ a -.");
        }
        return value;
    }

    public static string Save(StructureTemplate template, string? directory = null)
    {
        ArgumentNullException.ThrowIfNull(template);
        template.Name = ValidateName(template.Name);
        directory ??= DefaultDirectory;
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, template.Name + ".tstructure.json");
        string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temporary, JsonSerializer.Serialize(template, JsonOptions));
        File.Move(temporary, path, overwrite: true);
        return path;
    }

    public static StructureTemplate Load(string name, string? directory = null)
    {
        directory ??= DefaultDirectory;
        string path = Path.Combine(directory, ValidateName(name) + ".tstructure.json");
        StructureTemplate template = JsonSerializer.Deserialize<StructureTemplate>(
            File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException($"Struktura '{name}' je prázdná.");
        template.Name = ValidateName(template.Name);
        return template;
    }

    public static IReadOnlyList<StructureTemplate> LoadAll(string? directory = null)
    {
        directory ??= DefaultDirectory;
        if (!Directory.Exists(directory)) return [];
        var result = new List<StructureTemplate>();
        foreach (string path in Directory.GetFiles(directory, "*.tstructure.json")
                     .OrderBy(value => value, StringComparer.Ordinal))
        {
            try
            {
                StructureTemplate? template = JsonSerializer.Deserialize<StructureTemplate>(
                    File.ReadAllText(path), JsonOptions);
                if (template is not null)
                {
                    template.Name = ValidateName(template.Name);
                    result.Add(template);
                }
            }
            catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException)
            {
                // Jeden rozbitý uživatelský blueprint nesmí znemožnit vytvoření světa.
            }
        }
        return result;
    }
}
