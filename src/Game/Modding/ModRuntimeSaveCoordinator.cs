using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tesseris.Game.Entities;
using Tesseris.Game.Items;

namespace Tesseris.Game.Modding;

public enum ModRuntimeLoadStatus
{
    Loaded,
    RecoveredPreviousGeneration,
    LegacyWorldWithoutRuntimeSave
}

public enum ModRuntimeSavePhase
{
    StagingComplete,
    GenerationPublished,
    PointerCommitted
}

public sealed record ModRuntimeSaveLimits(
    long MaximumFileBytes = 256L * 1024 * 1024,
    long MaximumTotalBytes = 512L * 1024 * 1024,
    int MaximumManifestBytes = 1024 * 1024,
    int RetainedGenerations = 3)
{
    internal void Validate()
    {
        if (MaximumFileBytes <= 0) throw new ArgumentOutOfRangeException(nameof(MaximumFileBytes));
        if (MaximumTotalBytes < MaximumFileBytes) throw new ArgumentOutOfRangeException(nameof(MaximumTotalBytes));
        if (MaximumManifestBytes <= 0) throw new ArgumentOutOfRangeException(nameof(MaximumManifestBytes));
        if (RetainedGenerations < 2) throw new ArgumentOutOfRangeException(nameof(RetainedGenerations));
    }
}

public sealed record ModRuntimeLoadResult(
    ModRuntimeLoadStatus Status,
    ulong SimulationTick,
    EntityWorld? Entities,
    ModStackPlatform? Stacks,
    ModContainerPlatform? Containers,
    string? Generation)
{
    public bool HasRuntimeState => Entities is not null && Stacks is not null && Containers is not null;
}

/// <summary>
/// Commits all generic mod runtime stores as one world-scoped generation. The pointer is the commit
/// record: a fully written but unpointed generation is an orphan and is never treated as current.
/// </summary>
public sealed class ModRuntimeSaveCoordinator
{
    private const int FormatVersion = 1;
    private const string SaveDirectoryName = "mod-runtime-save";
    private const string GenerationsDirectoryName = "generations";
    private const string CurrentFileName = "current.json";
    private static readonly string[] DataFiles = ["containers.bin", "entities.bin", "stacks.bin"];

    private readonly int mainThreadId = Environment.CurrentManagedThreadId;
    private readonly string saveRoot;
    private readonly string generationsRoot;
    private readonly ModRuntimeSaveLimits limits;
    private readonly Action<ModRuntimeSavePhase>? phaseHook;

    public ModRuntimeSaveCoordinator(
        string worldDirectory,
        ModRuntimeSaveLimits? limits = null,
        Action<ModRuntimeSavePhase>? phaseHook = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worldDirectory);
        this.limits = limits ?? new ModRuntimeSaveLimits();
        this.limits.Validate();
        string worldRoot = Path.GetFullPath(worldDirectory);
        saveRoot = Path.GetFullPath(Path.Combine(worldRoot, SaveDirectoryName));
        EnsureDirectChild(worldRoot, saveRoot, SaveDirectoryName);
        generationsRoot = Path.GetFullPath(Path.Combine(saveRoot, GenerationsDirectoryName));
        EnsureDirectChild(saveRoot, generationsRoot, GenerationsDirectoryName);
        this.phaseHook = phaseHook;
    }

    public string SaveRoot => saveRoot;

    /// <summary>
    /// Calls <paramref name="quiesce"/> and snapshots every subsystem synchronously on the creating
    /// game thread before publishing a generation.
    /// </summary>
    public string Save(
        EntityWorld entities,
        ModStackPlatform stacks,
        ModContainerPlatform containers,
        ulong simulationTick,
        Action? quiesce = null)
    {
        EnsureMainThread();
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentNullException.ThrowIfNull(stacks);
        ArgumentNullException.ThrowIfNull(containers);
        EnsureSafeRoots(create: true);
        IReadOnlyList<PointerGeneration> previousHistory = ReadCommittedHistory();
        quiesce?.Invoke();

        byte[] entityBytes = SaveToBytes(entities.Save);
        byte[] stackBytes = stacks.SaveToBytes();
        byte[] containerBytes = containers.SaveToBytes();
        ValidateBlob(entityBytes, "entities.bin");
        ValidateBlob(stackBytes, "stacks.bin");
        ValidateBlob(containerBytes, "containers.bin");
        long total = checked((long)entityBytes.Length + stackBytes.Length + containerBytes.Length);
        if (total > limits.MaximumTotalBytes)
            throw new InvalidOperationException($"Runtime save size {total} exceeds {limits.MaximumTotalBytes} bytes.");

        ulong number = NextGenerationNumber();
        string generation = GenerationName(number);
        string stagingName = $"staging-{generation}-{Guid.NewGuid():N}";
        string staging = SafeChild(generationsRoot, stagingName);
        string published = SafeChild(generationsRoot, generation);
        Directory.CreateDirectory(staging);

        WriteDurable(Path.Combine(staging, "entities.bin"), entityBytes);
        WriteDurable(Path.Combine(staging, "stacks.bin"), stackBytes);
        WriteDurable(Path.Combine(staging, "containers.bin"), containerBytes);
        FileEntry[] entries =
        [
            Entry("containers.bin", containerBytes),
            Entry("entities.bin", entityBytes),
            Entry("stacks.bin", stackBytes)
        ];
        byte[] manifest = CreateManifest(generation, simulationTick, entries);
        if (manifest.Length > limits.MaximumManifestBytes)
            throw new InvalidOperationException("Runtime save manifest exceeds its configured limit.");
        byte[] manifestHash = Encoding.ASCII.GetBytes(Hash(manifest));
        WriteDurable(Path.Combine(staging, "manifest.json"), manifest);
        WriteDurable(Path.Combine(staging, "manifest.sha256"), manifestHash);
        phaseHook?.Invoke(ModRuntimeSavePhase.StagingComplete);

        Directory.Move(staging, published);
        phaseHook?.Invoke(ModRuntimeSavePhase.GenerationPublished);

        var history = new List<PointerGeneration>
        {
            new(generation, Encoding.ASCII.GetString(manifestHash))
        };
        history.AddRange(previousHistory
            .Where(value => value.Generation != generation)
            .Take(limits.RetainedGenerations - 1));
        byte[] pointer = CreatePointer(history);
        AtomicWrite(Path.Combine(saveRoot, CurrentFileName), pointer);
        phaseHook?.Invoke(ModRuntimeSavePhase.PointerCommitted);
        CleanupOrphans(history);
        return generation;
    }

    /// <summary>
    /// Validates every byte before constructing any runtime. Factories must return new, frozen, empty
    /// stack/container platforms; they are never exposed when any later load step fails.
    /// </summary>
    public ModRuntimeLoadResult Load(
        EntityRegistry entityRegistry,
        Func<ModStackPlatform> createStackPlatform,
        Func<ModContainerPlatform> createContainerPlatform)
    {
        EnsureMainThread();
        ArgumentNullException.ThrowIfNull(entityRegistry);
        ArgumentNullException.ThrowIfNull(createStackPlatform);
        ArgumentNullException.ThrowIfNull(createContainerPlatform);
        EnsureSafeRoots(create: false);
        string currentPath = Path.Combine(saveRoot, CurrentFileName);
        if (!File.Exists(currentPath))
        {
            return new ModRuntimeLoadResult(
                ModRuntimeLoadStatus.LegacyWorldWithoutRuntimeSave, 0, null, null, null, null);
        }

        EnsureRegularFile(currentPath);
        byte[] pointerBytes = ReadLimited(currentPath, limits.MaximumManifestBytes, "runtime pointer");
        CurrentPointer pointer = ParsePointer(pointerBytes);

        Exception? lastFailure = null;
        for (int candidateIndex = 0; candidateIndex < pointer.Generations.Count; candidateIndex++)
        {
            PointerGeneration candidate = pointer.Generations[candidateIndex];
            string generation = candidate.Generation;
            try
            {
                ValidatedGeneration validated = ValidateGeneration(generation, candidate.ManifestSha256);
                EntityWorld entities = EntityWorld.Load(entityRegistry, new MemoryStream(validated.Entities, writable: false));
                ModStackPlatform stacks = createStackPlatform()
                    ?? throw new InvalidOperationException("The stack platform factory returned null.");
                ModContainerPlatform containers = createContainerPlatform()
                    ?? throw new InvalidOperationException("The container platform factory returned null.");
                stacks.Load(new MemoryStream(validated.Stacks, writable: false));
                containers.Load(new MemoryStream(validated.Containers, writable: false));
                return new ModRuntimeLoadResult(
                    candidateIndex == 0
                        ? ModRuntimeLoadStatus.Loaded
                        : ModRuntimeLoadStatus.RecoveredPreviousGeneration,
                    validated.Tick,
                    entities,
                    stacks,
                    containers,
                    generation);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException)
            {
                lastFailure = exception;
            }
        }

        throw new InvalidDataException(
            $"No valid committed mod runtime generation exists at '{saveRoot}'.",
            lastFailure);
    }

    private ValidatedGeneration ValidateGeneration(string generation, string? expectedManifestHash)
    {
        _ = ParseGeneration(generation);
        string directory = SafeChild(generationsRoot, generation);
        EnsureRealDirectory(directory);
        string manifestPath = Path.Combine(directory, "manifest.json");
        string hashPath = Path.Combine(directory, "manifest.sha256");
        EnsureRegularFile(manifestPath);
        EnsureRegularFile(hashPath);
        byte[] manifestBytes = ReadLimited(manifestPath, limits.MaximumManifestBytes, "runtime manifest");
        string actualManifestHash = Hash(manifestBytes);
        string storedManifestHash = Encoding.ASCII.GetString(ReadLimited(hashPath, 128, "manifest checksum")).Trim();
        ValidateHash(storedManifestHash, "manifest checksum");
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(actualManifestHash), Encoding.ASCII.GetBytes(storedManifestHash)))
            throw new InvalidDataException("Runtime manifest checksum mismatch.");
        if (expectedManifestHash is not null
            && !string.Equals(actualManifestHash, expectedManifestHash, StringComparison.Ordinal))
            throw new InvalidDataException("Current pointer manifest checksum mismatch.");

        Manifest manifest = ParseManifest(manifestBytes);
        if (manifest.Generation != generation) throw new InvalidDataException("Manifest generation does not match its directory.");
        if (manifest.Files.Count != DataFiles.Length) throw new InvalidDataException("Manifest file set is incomplete.");
        var blobs = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        long total = 0;
        foreach (string name in DataFiles)
        {
            if (!manifest.Files.TryGetValue(name, out FileEntry? entry))
                throw new InvalidDataException($"Manifest is missing '{name}'.");
            if (entry.Length < 0 || entry.Length > limits.MaximumFileBytes)
                throw new InvalidDataException($"Manifest length for '{name}' exceeds limits.");
            ValidateHash(entry.Sha256, $"{name} checksum");
            string path = SafeChild(directory, name);
            EnsureRegularFile(path);
            var info = new FileInfo(path);
            if (info.Length != entry.Length) throw new InvalidDataException($"Length mismatch for '{name}'.");
            byte[] bytes = ReadLimited(path, limits.MaximumFileBytes, name);
            if (!string.Equals(Hash(bytes), entry.Sha256, StringComparison.Ordinal))
                throw new InvalidDataException($"Checksum mismatch for '{name}'.");
            blobs.Add(name, bytes);
            total = checked(total + bytes.Length);
            if (total > limits.MaximumTotalBytes) throw new InvalidDataException("Runtime save exceeds total size limit.");
        }

        return new ValidatedGeneration(
            manifest.Tick, blobs["entities.bin"], blobs["stacks.bin"], blobs["containers.bin"]);
    }

    private void CleanupOrphans(IReadOnlyList<PointerGeneration> committedHistory)
    {
        EnsureRealDirectory(generationsRoot);
        var keep = committedHistory.Select(value => value.Generation).ToHashSet(StringComparer.Ordinal);
        foreach (string path in Directory.EnumerateFileSystemEntries(generationsRoot))
        {
            string name = Path.GetFileName(path);
            bool managedStaging = name.StartsWith("staging-gen-", StringComparison.Ordinal);
            bool unreferencedGeneration = TryParseGeneration(name, out _) && !keep.Contains(name);
            if (managedStaging || unreferencedGeneration) DeleteManagedEntry(path);
        }
    }

    private static byte[] CreateManifest(string generation, ulong tick, IReadOnlyList<FileEntry> files)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("formatVersion", FormatVersion);
            writer.WriteString("generation", generation);
            writer.WriteNumber("simulationTick", tick);
            writer.WriteStartArray("files");
            foreach (FileEntry file in files.OrderBy(value => value.Name, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("name", file.Name);
                writer.WriteNumber("length", file.Length);
                writer.WriteString("sha256", file.Sha256);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    private static byte[] CreatePointer(IReadOnlyList<PointerGeneration> generations)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("formatVersion", FormatVersion);
            writer.WriteStartArray("generations");
            foreach (PointerGeneration generation in generations)
            {
                writer.WriteStartObject();
                writer.WriteString("generation", generation.Generation);
                writer.WriteString("manifestSha256", generation.ManifestSha256);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    private static CurrentPointer ParsePointer(byte[] bytes)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(bytes);
            JsonElement root = document.RootElement;
            if (root.GetProperty("formatVersion").GetInt32() != FormatVersion)
                throw new InvalidDataException("Unsupported runtime pointer version.");
            var generations = new List<PointerGeneration>();
            ulong previous = ulong.MaxValue;
            foreach (JsonElement item in root.GetProperty("generations").EnumerateArray())
            {
                string generation = RequiredString(item, "generation");
                ulong number = ParseGeneration(generation);
                if (number >= previous) throw new InvalidDataException("Runtime pointer history is not descending.");
                previous = number;
                string hash = RequiredString(item, "manifestSha256");
                ValidateHash(hash, "pointer manifest checksum");
                generations.Add(new PointerGeneration(generation, hash));
            }
            if (generations.Count == 0) throw new InvalidDataException("Runtime pointer history is empty.");
            return new CurrentPointer(Array.AsReadOnly(generations.ToArray()));
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new InvalidDataException("The runtime pointer is invalid.", exception);
        }
    }

    private static Manifest ParseManifest(byte[] bytes)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(bytes);
            JsonElement root = document.RootElement;
            if (root.GetProperty("formatVersion").GetInt32() != FormatVersion)
                throw new InvalidDataException("Unsupported runtime manifest version.");
            string generation = RequiredString(root, "generation");
            _ = ParseGeneration(generation);
            ulong tick = root.GetProperty("simulationTick").GetUInt64();
            var files = new Dictionary<string, FileEntry>(StringComparer.Ordinal);
            foreach (JsonElement item in root.GetProperty("files").EnumerateArray())
            {
                string name = RequiredString(item, "name");
                if (!DataFiles.Contains(name, StringComparer.Ordinal))
                    throw new InvalidDataException($"Unexpected runtime file '{name}'.");
                long length = item.GetProperty("length").GetInt64();
                string sha = RequiredString(item, "sha256");
                if (!files.TryAdd(name, new FileEntry(name, length, sha)))
                    throw new InvalidDataException($"Duplicate runtime file '{name}'.");
            }
            return new Manifest(generation, tick, files);
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new InvalidDataException("The runtime manifest is invalid.", exception);
        }
    }

    private void EnsureSafeRoots(bool create)
    {
        if (Directory.Exists(saveRoot)) EnsureRealDirectory(saveRoot);
        else if (create) Directory.CreateDirectory(saveRoot);
        else return;
        if (Directory.Exists(generationsRoot)) EnsureRealDirectory(generationsRoot);
        else if (create) Directory.CreateDirectory(generationsRoot);
    }

    private IReadOnlyList<PointerGeneration> ReadCommittedHistory()
    {
        string path = Path.Combine(saveRoot, CurrentFileName);
        if (!File.Exists(path)) return [];
        EnsureRegularFile(path);
        return ParsePointer(ReadLimited(path, limits.MaximumManifestBytes, "runtime pointer")).Generations;
    }

    private ulong NextGenerationNumber()
    {
        ulong highest = 0;
        foreach (string path in Directory.EnumerateDirectories(generationsRoot))
        {
            if (TryParseGeneration(Path.GetFileName(path), out ulong value)) highest = Math.Max(highest, value);
        }
        if (highest == ulong.MaxValue) throw new InvalidOperationException("Runtime generation space exhausted.");
        return highest + 1;
    }

    private static string GenerationName(ulong number) => $"gen-{number:X16}";

    private static ulong ParseGeneration(string name)
    {
        if (!TryParseGeneration(name, out ulong value))
            throw new InvalidDataException($"Invalid runtime generation name '{name}'.");
        return value;
    }

    private static bool TryParseGeneration(string? name, out ulong value)
    {
        value = 0;
        return name is { Length: 20 }
               && name.StartsWith("gen-", StringComparison.Ordinal)
               && ulong.TryParse(name.AsSpan(4), System.Globalization.NumberStyles.AllowHexSpecifier,
                   System.Globalization.CultureInfo.InvariantCulture, out value)
               && value != 0;
    }

    private static void WriteDurable(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024,
            FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static void AtomicWrite(string path, byte[] bytes)
    {
        string temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            WriteDurable(temporary, bytes);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static byte[] SaveToBytes(Action<Stream> save)
    {
        using var stream = new MemoryStream();
        save(stream);
        return stream.ToArray();
    }

    private void ValidateBlob(byte[] bytes, string name)
    {
        if (bytes.LongLength > limits.MaximumFileBytes)
            throw new InvalidOperationException($"Runtime file '{name}' exceeds {limits.MaximumFileBytes} bytes.");
    }

    private static FileEntry Entry(string name, byte[] bytes) => new(name, bytes.LongLength, Hash(bytes));
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static void ValidateHash(string value, string description)
    {
        if (value.Length != 64 || value.Any(character => !char.IsAsciiHexDigit(character) || char.IsUpper(character)))
            throw new InvalidDataException($"Invalid {description}.");
    }

    private static byte[] ReadLimited(string path, long maximum, string description)
    {
        var info = new FileInfo(path);
        if (info.Length < 0 || info.Length > maximum || info.Length > int.MaxValue)
            throw new InvalidDataException($"The {description} exceeds its configured limit.");
        return File.ReadAllBytes(path);
    }

    private static string RequiredString(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out JsonElement value)
            || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
            throw new InvalidDataException($"Required string '{property}' is missing.");
        return value.GetString()!;
    }

    private static string SafeChild(string parent, string name)
    {
        if (name != Path.GetFileName(name) || name is "." or "..")
            throw new InvalidDataException($"Unsafe runtime save path segment '{name}'.");
        string child = Path.GetFullPath(Path.Combine(parent, name));
        EnsureDirectChild(parent, child, name);
        return child;
    }

    private static void EnsureDirectChild(string parent, string child, string description)
    {
        string relative = Path.GetRelativePath(Path.GetFullPath(parent), Path.GetFullPath(child));
        if (relative == "." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || relative == ".." || Path.IsPathRooted(relative) || relative.Contains(Path.DirectorySeparatorChar))
            throw new InvalidDataException($"Unsafe runtime save path '{description}'.");
    }

    private static void EnsureRealDirectory(string path)
    {
        var info = new DirectoryInfo(path);
        if (!info.Exists) throw new DirectoryNotFoundException(path);
        if (info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException($"Runtime save directory '{path}' must not be a link or reparse point.");
    }

    private static void EnsureRegularFile(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("Runtime save file is missing.", path);
        if (info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException($"Runtime save file '{path}' must not be a link or reparse point.");
    }

    private static void DeleteManagedEntry(string path)
    {
        FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        if (info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            if (info is DirectoryInfo) Directory.Delete(path, recursive: false);
            else File.Delete(path);
            return;
        }
        if (info is DirectoryInfo) Directory.Delete(path, recursive: true);
        else File.Delete(path);
    }

    private void EnsureMainThread()
    {
        if (Environment.CurrentManagedThreadId != mainThreadId)
            throw new InvalidOperationException("Mod runtime save coordination must run on the game thread.");
    }

    private sealed record CurrentPointer(IReadOnlyList<PointerGeneration> Generations);
    private sealed record PointerGeneration(string Generation, string ManifestSha256);
    private sealed record FileEntry(string Name, long Length, string Sha256);
    private sealed record Manifest(string Generation, ulong Tick, IReadOnlyDictionary<string, FileEntry> Files);
    private sealed record ValidatedGeneration(ulong Tick, byte[] Entities, byte[] Stacks, byte[] Containers);
}
