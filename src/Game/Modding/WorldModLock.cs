using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tesseris.ModApi;

namespace Tesseris.Game.Modding;

/// <summary>One exact mod package recorded in a world's mod lock.</summary>
public sealed record WorldModLockEntry(string Id, string Version, string Sha256);

/// <summary>The deterministic inputs that can affect persisted world data and generation.</summary>
public sealed record WorldModLockSnapshot(
    int FormatVersion,
    string ModApiVersion,
    int WorldGenerationPipelineVersion,
    IReadOnlyList<WorldModLockEntry> Mods,
    string? WorldPresetId = null,
    string? WorldDimensionId = null,
    string? WorldDefinitionFingerprint = null);

public enum WorldModLockReadStatus
{
    Valid,
    Missing,
    Invalid,
}

public sealed record WorldModLockReadResult(
    WorldModLockReadStatus Status,
    WorldModLockSnapshot? Snapshot,
    string? Error)
{
    public bool IsValid => Status == WorldModLockReadStatus.Valid;
}

public enum WorldModLockComparisonStatus
{
    Match,
    Different,
    MissingLock,
    InvalidLock,
}

public sealed record ChangedWorldMod(WorldModLockEntry Saved, WorldModLockEntry Current);

/// <summary>A structured comparison suitable for both a menu warning and a hard server check.</summary>
public sealed record WorldModLockComparison(
    WorldModLockComparisonStatus Status,
    bool ModApiChanged,
    bool WorldGenerationPipelineChanged,
    bool WorldDefinitionChanged,
    IReadOnlyList<WorldModLockEntry> Added,
    IReadOnlyList<WorldModLockEntry> Removed,
    IReadOnlyList<ChangedWorldMod> Changed,
    string? Error)
{
    public bool IsMatch => Status == WorldModLockComparisonStatus.Match;
}

/// <summary>
/// Captures and persists the exact modpack used by a world. Directory hashes deliberately omit
/// build/cache directories, so rebuilding a development mod does not include compiler scratch data.
/// </summary>
public static class WorldModLock
{
    public const string FileName = "tesseris.mods.lock.json";
    public const string LegacyFileName = "voxelity.mods.lock.json";
    public const int CurrentFormatVersion = 1;
    public const int CurrentWorldGenerationPipelineVersion = 1;

    private static readonly HashSet<string> VolatileDirectories = new(
        ["bin", "obj", "cache", ".cache", ".git", ".hg", ".svn"],
        StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static WorldModLockSnapshot Capture(
        IEnumerable<LoadedModInfo> loadedMods,
        int worldGenerationPipelineVersion = CurrentWorldGenerationPipelineVersion,
        string? worldPresetId = null,
        string? worldDimensionId = null,
        string? worldDefinitionFingerprint = null)
    {
        ArgumentNullException.ThrowIfNull(loadedMods);
        if (worldGenerationPipelineVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(worldGenerationPipelineVersion),
                "The world-generation pipeline version must be positive.");
        }

        LoadedModInfo[] sorted = loadedMods
            .OrderBy(mod => mod.Descriptor.Id, StringComparer.Ordinal)
            .ToArray();

        for (int index = 1; index < sorted.Length; index++)
        {
            if (string.Equals(
                    sorted[index - 1].Descriptor.Id,
                    sorted[index].Descriptor.Id,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Mod '{sorted[index].Descriptor.Id}' occurs more than once in the loaded modpack.");
            }
        }

        WorldModLockEntry[] entries = sorted
            .Select(mod => new WorldModLockEntry(
                mod.Descriptor.Id,
                mod.Descriptor.Version,
                ComputeDirectoryHash(mod.Descriptor.Directory)))
            .ToArray();

        return new WorldModLockSnapshot(
            CurrentFormatVersion,
            ModApiInfo.CurrentVersion,
            worldGenerationPipelineVersion,
            entries,
            worldPresetId,
            worldDimensionId,
            worldDefinitionFingerprint);
    }

    /// <summary>Computes a platform-independent SHA-256 over sorted relative paths and file bytes.</summary>
    public static string ComputeDirectoryHash(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        string root = Path.GetFullPath(directory);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Mod directory does not exist: {root}");
        }

        var enumeration = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = false,
            ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };

        (string Relative, string Full)[] files = Directory
            .EnumerateFiles(root, "*", enumeration)
            .Select(path => (Relative: NormalizeRelativePath(Path.GetRelativePath(root, path)), Full: path))
            .Where(file => !ContainsVolatileDirectory(file.Relative))
            .OrderBy(file => file.Relative, StringComparer.Ordinal)
            .ToArray();

        using IncrementalHash directoryHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        // Format v1 domain is a persisted protocol constant; keep it for old-world hash parity.
        directoryHash.AppendData("Voxelity.ModDirectoryHash.v1\0"u8);

        Span<byte> length = stackalloc byte[sizeof(int)];
        foreach ((string relative, string full) in files)
        {
            byte[] pathBytes = Encoding.UTF8.GetBytes(relative);
            BinaryPrimitives.WriteInt32LittleEndian(length, pathBytes.Length);
            directoryHash.AppendData(length);
            directoryHash.AppendData(pathBytes);

            using FileStream stream = new(full, FileMode.Open, FileAccess.Read, FileShare.Read);
            byte[] fileHash = SHA256.HashData(stream);
            directoryHash.AppendData(fileHash);
        }

        return Convert.ToHexString(directoryHash.GetHashAndReset()).ToLowerInvariant();
    }

    /// <summary>Writes through a temporary sibling and renames it over the destination.</summary>
    public static void Write(string worldDirectory, WorldModLockSnapshot snapshot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worldDirectory);
        ArgumentNullException.ThrowIfNull(snapshot);
        Validate(snapshot);

        string root = Path.GetFullPath(worldDirectory);
        Directory.CreateDirectory(root);
        string destination = Path.Combine(root, FileName);
        string temporary = Path.Combine(root, $".{FileName}.{Guid.NewGuid():N}.tmp");

        try
        {
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions);
            using (var stream = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4096,
                       FileOptions.WriteThrough))
            {
                stream.Write(json);
                stream.WriteByte((byte)'\n');
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    public static WorldModLockReadResult Read(string worldDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worldDirectory);
        string root = Path.GetFullPath(worldDirectory);
        string path = Path.Combine(root, FileName);
        if (!File.Exists(path))
        {
            path = Path.Combine(root, LegacyFileName);
        }
        if (!File.Exists(path))
        {
            return new WorldModLockReadResult(WorldModLockReadStatus.Missing, null, "The world has no mod lock.");
        }

        try
        {
            WorldModLockSnapshot? snapshot = JsonSerializer.Deserialize<WorldModLockSnapshot>(
                File.ReadAllText(path),
                JsonOptions);
            if (snapshot is null)
            {
                throw new InvalidDataException("The mod lock is empty.");
            }

            Validate(snapshot);
            return new WorldModLockReadResult(WorldModLockReadStatus.Valid, snapshot, null);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or JsonException
                or NotSupportedException
                or InvalidDataException)
        {
            return new WorldModLockReadResult(WorldModLockReadStatus.Invalid, null, exception.Message);
        }
    }

    public static WorldModLockComparison Compare(
        WorldModLockReadResult savedLock,
        WorldModLockSnapshot current)
    {
        ArgumentNullException.ThrowIfNull(savedLock);
        ArgumentNullException.ThrowIfNull(current);
        Validate(current);

        if (savedLock.Status == WorldModLockReadStatus.Missing)
        {
            return EmptyComparison(WorldModLockComparisonStatus.MissingLock, savedLock.Error);
        }

        if (savedLock.Status == WorldModLockReadStatus.Invalid || savedLock.Snapshot is null)
        {
            return EmptyComparison(WorldModLockComparisonStatus.InvalidLock, savedLock.Error);
        }

        return Compare(savedLock.Snapshot, current);
    }

    public static WorldModLockComparison Compare(
        WorldModLockSnapshot saved,
        WorldModLockSnapshot current)
    {
        ArgumentNullException.ThrowIfNull(saved);
        ArgumentNullException.ThrowIfNull(current);
        Validate(saved);
        Validate(current);

        Dictionary<string, WorldModLockEntry> savedById = saved.Mods.ToDictionary(mod => mod.Id, StringComparer.Ordinal);
        Dictionary<string, WorldModLockEntry> currentById = current.Mods.ToDictionary(mod => mod.Id, StringComparer.Ordinal);

        WorldModLockEntry[] added = current.Mods
            .Where(mod => !savedById.ContainsKey(mod.Id))
            .ToArray();
        WorldModLockEntry[] removed = saved.Mods
            .Where(mod => !currentById.ContainsKey(mod.Id))
            .ToArray();
        ChangedWorldMod[] changed = current.Mods
            .Where(mod => savedById.TryGetValue(mod.Id, out WorldModLockEntry? old) && old != mod)
            .Select(mod => new ChangedWorldMod(savedById[mod.Id], mod))
            .ToArray();

        bool apiChanged = !string.Equals(saved.ModApiVersion, current.ModApiVersion, StringComparison.Ordinal);
        bool pipelineChanged = saved.WorldGenerationPipelineVersion != current.WorldGenerationPipelineVersion;
        bool definitionChanged = saved.WorldDefinitionFingerprint is not null
            && current.WorldDefinitionFingerprint is not null
            && (!string.Equals(saved.WorldDefinitionFingerprint, current.WorldDefinitionFingerprint, StringComparison.Ordinal)
                || !string.Equals(saved.WorldPresetId, current.WorldPresetId, StringComparison.Ordinal)
                || !string.Equals(saved.WorldDimensionId, current.WorldDimensionId, StringComparison.Ordinal));
        bool different = apiChanged || pipelineChanged || definitionChanged
            || added.Length != 0 || removed.Length != 0 || changed.Length != 0;

        return new WorldModLockComparison(
            different ? WorldModLockComparisonStatus.Different : WorldModLockComparisonStatus.Match,
            apiChanged,
            pipelineChanged,
            definitionChanged,
            added,
            removed,
            changed,
            null);
    }

    private static WorldModLockComparison EmptyComparison(WorldModLockComparisonStatus status, string? error) =>
        new(status, false, false, false, [], [], [], error);

    private static string NormalizeRelativePath(string path) => path.Replace('\\', '/');

    private static bool ContainsVolatileDirectory(string relativePath)
    {
        string[] segments = relativePath.Split('/');
        for (int index = 0; index < segments.Length - 1; index++)
        {
            if (VolatileDirectories.Contains(segments[index]))
            {
                return true;
            }
        }

        return false;
    }

    private static void Validate(WorldModLockSnapshot snapshot)
    {
        if (snapshot.FormatVersion != CurrentFormatVersion)
        {
            throw new InvalidDataException(
                $"Unsupported mod lock format {snapshot.FormatVersion}; expected {CurrentFormatVersion}.");
        }

        if (!SemanticVersion.TryParse(snapshot.ModApiVersion, out _))
        {
            throw new InvalidDataException("The mod API version is not a semantic version.");
        }

        if (snapshot.WorldGenerationPipelineVersion <= 0)
        {
            throw new InvalidDataException("The world-generation pipeline version must be positive.");
        }

        if (snapshot.WorldPresetId is not null)
        {
            _ = new Tesseris.ModApi.ResourceId(snapshot.WorldPresetId);
        }

        if (snapshot.WorldDimensionId is not null)
        {
            _ = new Tesseris.ModApi.ResourceId(snapshot.WorldDimensionId);
        }

        if (snapshot.WorldDefinitionFingerprint is not null
            && (snapshot.WorldDefinitionFingerprint.Length != 64
                || !snapshot.WorldDefinitionFingerprint.All(Uri.IsHexDigit)))
        {
            throw new InvalidDataException("The world-definition fingerprint is not a SHA-256 value.");
        }

        if (snapshot.Mods is null)
        {
            throw new InvalidDataException("The mod list is missing.");
        }

        string? previousId = null;
        foreach (WorldModLockEntry mod in snapshot.Mods)
        {
            if (mod is null || !IsValidModId(mod.Id))
            {
                throw new InvalidDataException($"Invalid mod ID '{mod?.Id}'.");
            }

            if (previousId is not null && string.CompareOrdinal(previousId, mod.Id) >= 0)
            {
                throw new InvalidDataException("The mod list must be unique and sorted by ID.");
            }

            if (!SemanticVersion.TryParse(mod.Version, out _))
            {
                throw new InvalidDataException($"Mod '{mod.Id}' has an invalid semantic version.");
            }

            if (mod.Sha256.Length != 64 || mod.Sha256.Any(character =>
                    !((character >= '0' && character <= '9') || (character >= 'a' && character <= 'f'))))
            {
                throw new InvalidDataException($"Mod '{mod.Id}' has an invalid SHA-256 hash.");
            }

            previousId = mod.Id;
        }
    }

    private static bool IsValidModId(string? id) =>
        !string.IsNullOrWhiteSpace(id)
        && id.All(character =>
            (character >= 'a' && character <= 'z')
            || (character >= '0' && character <= '9')
            || character is '_' or '-' or '.');
}
