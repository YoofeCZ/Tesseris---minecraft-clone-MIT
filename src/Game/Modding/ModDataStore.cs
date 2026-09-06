using System.Text.Json;
using Tesseris.ModApi;

namespace Tesseris.Game.Modding;

/// <summary>
/// Owns opaque, namespaced mod data for one open world. A view returned by <see cref="ForMod"/>
/// remains stable across world changes, while every container obtained from that view is valid only
/// for the world generation in which it was created.
/// </summary>
public sealed class ModDataStore
{
    public const string DirectoryName = "moddata";
    public const string FileExtension = ".vmd";
    public const int CurrentFormatVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly int mainThreadId = Environment.CurrentManagedThreadId;
    private readonly Dictionary<string, ModView> views = new(StringComparer.Ordinal);
    private Session? session;
    private long generation;

    public bool IsWorldOpen
    {
        get
        {
            EnsureMainThread();
            return session is not null;
        }
    }

    /// <summary>
    /// Returns the stable public view for one loaded mod. All views must be requested before a world
    /// is attached, which prevents a late registration from accidentally skipping its on-disk data.
    /// </summary>
    public IModData ForMod(string modId)
    {
        EnsureMainThread();
        ValidateModId(modId);

        if (views.TryGetValue(modId, out ModView? existing))
        {
            return existing;
        }

        if (session is not null)
        {
            throw new InvalidOperationException("Mod data views must be registered before a world is attached.");
        }

        var created = new ModView(this, modId);
        views.Add(modId, created);
        return created;
    }

    /// <summary>
    /// Opens a world and loads only the registered mods. Files belonging to absent mods are neither
    /// enumerated nor touched.
    /// </summary>
    public void AttachWorld(string worldDirectory)
    {
        EnsureMainThread();
        ArgumentException.ThrowIfNullOrWhiteSpace(worldDirectory);
        if (session is not null)
        {
            throw new InvalidOperationException("A mod-data world is already attached.");
        }

        string root = Path.GetFullPath(worldDirectory);
        var loaded = new Dictionary<string, OwnerState>(StringComparer.Ordinal);

        // Build the entire session first. A corrupt owner file therefore leaves the store closed and
        // cannot later be overwritten by a partially initialized empty state.
        foreach (string owner in views.Keys.Order(StringComparer.Ordinal))
        {
            loaded.Add(owner, LoadOwner(root, owner));
        }

        session = new Session(root, checked(++generation), loaded);
    }

    /// <summary>Atomically saves every dirty loaded mod into its own deterministic file.</summary>
    public void SaveWorld()
    {
        EnsureMainThread();
        Session current = RequireSession();

        foreach ((string owner, OwnerState state) in current.Owners.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (!state.Dirty)
            {
                continue;
            }

            WriteOwner(current.WorldDirectory, owner, state);
            state.Dirty = false;
        }
    }

    /// <summary>
    /// Detaches the current world and invalidates all handed-out containers. Call <see cref="SaveWorld"/>
    /// first when dirty state should be retained.
    /// </summary>
    public void DetachWorld()
    {
        EnsureMainThread();
        if (session is null)
        {
            return;
        }

        session = null;
        checked { generation++; }
    }

    /// <summary>Removes every value owned by one mod at a position without scanning other records.</summary>
    public bool RemovePosition(string modId, ModBlockPosition position)
    {
        EnsureMainThread();
        ValidateModId(modId);
        Session current = RequireSession();
        if (!current.Owners.TryGetValue(modId, out OwnerState? owner))
        {
            throw new InvalidOperationException($"Mod '{modId}' has no registered data view.");
        }

        bool removed = owner.Positions.Remove(PositionKey.From(position));
        owner.Dirty |= removed;
        return removed;
    }

    public static string FileNameForMod(string modId)
    {
        ValidateModId(modId);
        return modId + FileExtension;
    }

    private static OwnerState LoadOwner(string worldDirectory, string owner)
    {
        string path = OwnerPath(worldDirectory, owner);
        if (!File.Exists(path))
        {
            return new OwnerState();
        }

        try
        {
            FileModel? model = JsonSerializer.Deserialize<FileModel>(File.ReadAllBytes(path), JsonOptions);
            if (model is null)
            {
                throw new InvalidDataException($"Mod data file '{path}' is empty.");
            }

            return Decode(model, owner, path);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or ArgumentException)
        {
            throw new InvalidDataException($"Mod data file '{path}' is corrupt: {exception.Message}", exception);
        }
    }

    private static OwnerState Decode(FileModel model, string owner, string path)
    {
        if (model.FormatVersion != CurrentFormatVersion)
        {
            throw new InvalidDataException(
                $"Mod data file '{path}' uses format {model.FormatVersion}; expected {CurrentFormatVersion}.");
        }

        if (!string.Equals(model.ModId, owner, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Mod data file '{path}' belongs to '{model.ModId}', not '{owner}'.");
        }

        if (model.World is null || model.Positions is null)
        {
            throw new InvalidDataException($"Mod data file '{path}' has missing collections.");
        }

        var state = new OwnerState();
        DecodeEntries(model.World, owner, state.World, path);

        foreach (PositionModel position in model.Positions)
        {
            if (position is null || position.Values is null)
            {
                throw new InvalidDataException($"Mod data file '{path}' contains an invalid position record.");
            }

            var values = new Dictionary<ResourceId, byte[]>();
            DecodeEntries(position.Values, owner, values, path);
            var key = new PositionKey(position.X, position.Y, position.Z);
            if (!state.Positions.TryAdd(key, values))
            {
                throw new InvalidDataException($"Mod data file '{path}' contains duplicate position {key}.");
            }
        }

        return state;
    }

    private static void DecodeEntries(
        IEnumerable<EntryModel> entries,
        string owner,
        Dictionary<ResourceId, byte[]> destination,
        string path)
    {
        foreach (EntryModel entry in entries)
        {
            if (entry is null || entry.Key is null || entry.Value is null)
            {
                throw new InvalidDataException($"Mod data file '{path}' contains an invalid value record.");
            }

            ResourceId key;
            try
            {
                key = new ResourceId(entry.Key);
                EnsureOwned(owner, key);
            }
            catch (ArgumentException exception)
            {
                throw new InvalidDataException(
                    $"Mod data file '{path}' contains invalid key '{entry.Key}'.",
                    exception);
            }

            if (!destination.TryAdd(key, entry.Value.ToArray()))
            {
                throw new InvalidDataException($"Mod data file '{path}' contains duplicate key '{key}'.");
            }
        }
    }

    private static void WriteOwner(string worldDirectory, string owner, OwnerState state)
    {
        string directory = Path.Combine(worldDirectory, DirectoryName);
        Directory.CreateDirectory(directory);
        string destination = OwnerPath(worldDirectory, owner);
        string temporary = Path.Combine(directory, $".{owner}.{Guid.NewGuid():N}.tmp");

        var model = new FileModel
        {
            FormatVersion = CurrentFormatVersion,
            ModId = owner,
            World = EncodeEntries(state.World),
            Positions = state.Positions
                .OrderBy(pair => pair.Key.X)
                .ThenBy(pair => pair.Key.Y)
                .ThenBy(pair => pair.Key.Z)
                .Select(pair => new PositionModel
                {
                    X = pair.Key.X,
                    Y = pair.Key.Y,
                    Z = pair.Key.Z,
                    Values = EncodeEntries(pair.Value),
                })
                .ToList(),
        };

        try
        {
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(model, JsonOptions);
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

            if (File.Exists(destination))
            {
                File.Replace(temporary, destination, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temporary, destination);
            }
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static List<EntryModel> EncodeEntries(Dictionary<ResourceId, byte[]> values) => values
        .OrderBy(pair => pair.Key.Value, StringComparer.Ordinal)
        .Select(pair => new EntryModel { Key = pair.Key.Value, Value = pair.Value.ToArray() })
        .ToList();

    private static string OwnerPath(string worldDirectory, string owner) =>
        Path.Combine(worldDirectory, DirectoryName, FileNameForMod(owner));

    private static void ValidateModId(string modId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modId);
        if (modId.Any(character =>
                !((character >= 'a' && character <= 'z')
                  || (character >= '0' && character <= '9')
                  || character is '_' or '-' or '.')))
        {
            throw new ArgumentException(
                "A mod ID may contain only lowercase letters, digits, '_', '-' or '.'.",
                nameof(modId));
        }
    }

    private static void EnsureOwned(string owner, ResourceId key)
    {
        string? value = key.Value;
        if (string.IsNullOrWhiteSpace(value)
            || !value.StartsWith(owner + ":", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Mod '{owner}' may only access data keys in its own namespace.",
                nameof(key));
        }
    }

    private Session RequireSession()
    {
        return session ?? throw new InvalidOperationException("No mod-data world is open.");
    }

    private Session RequireSession(long expectedGeneration)
    {
        Session current = RequireSession();
        if (current.Generation != expectedGeneration)
        {
            throw new InvalidOperationException("This mod-data container belongs to a stale world session.");
        }

        return current;
    }

    private OwnerState StateFor(string owner, long expectedGeneration)
    {
        EnsureMainThread();
        Session current = RequireSession(expectedGeneration);
        return current.Owners[owner];
    }

    private void EnsureMainThread()
    {
        if (Environment.CurrentManagedThreadId != mainThreadId)
        {
            throw new InvalidOperationException("Mod data may only be accessed on the main game thread.");
        }
    }

    private sealed class ModView : IModData
    {
        private readonly ModDataStore store;
        private readonly string owner;

        public ModView(ModDataStore store, string owner)
        {
            this.store = store;
            this.owner = owner;
        }

        public bool IsWorldOpen => store.IsWorldOpen;

        public IModDataContainer? World
        {
            get
            {
                store.EnsureMainThread();
                Session? current = store.session;
                return current is null ? null : new Container(store, owner, current.Generation, position: null);
            }
        }

        public IModDataContainer? At(ModBlockPosition position)
        {
            store.EnsureMainThread();
            Session? current = store.session;
            return current is null
                ? null
                : new Container(store, owner, current.Generation, PositionKey.From(position));
        }
    }

    private sealed class Container : IModDataContainer
    {
        private readonly ModDataStore store;
        private readonly string owner;
        private readonly long generation;
        private readonly PositionKey? position;

        public Container(ModDataStore store, string owner, long generation, PositionKey? position)
        {
            this.store = store;
            this.owner = owner;
            this.generation = generation;
            this.position = position;
        }

        public IReadOnlyCollection<ResourceId> Keys
        {
            get
            {
                Dictionary<ResourceId, byte[]> values = Values(create: false, out _);
                return values.Keys.OrderBy(key => key.Value, StringComparer.Ordinal).ToArray();
            }
        }

        public bool TryGet(ResourceId key, out ReadOnlyMemory<byte> value)
        {
            EnsureOwned(owner, key);
            Dictionary<ResourceId, byte[]> values = Values(create: false, out _);
            if (values.TryGetValue(key, out byte[]? stored))
            {
                value = new ReadOnlyMemory<byte>(stored.ToArray());
                return true;
            }

            value = default;
            return false;
        }

        public void Set(ResourceId key, ReadOnlySpan<byte> value)
        {
            EnsureOwned(owner, key);
            Dictionary<ResourceId, byte[]> values = Values(create: true, out OwnerState state);
            byte[] copy = value.ToArray();
            if (values.TryGetValue(key, out byte[]? old) && old.AsSpan().SequenceEqual(copy))
            {
                return;
            }

            values[key] = copy;
            state.Dirty = true;
        }

        public bool Remove(ResourceId key)
        {
            EnsureOwned(owner, key);
            Dictionary<ResourceId, byte[]> values = Values(create: false, out OwnerState state);
            bool removed = values.Remove(key);
            state.Dirty |= removed;
            return removed;
        }

        private Dictionary<ResourceId, byte[]> Values(bool create, out OwnerState state)
        {
            state = store.StateFor(owner, generation);
            if (position is null)
            {
                return state.World;
            }

            if (state.Positions.TryGetValue(position.Value, out Dictionary<ResourceId, byte[]>? values))
            {
                return values;
            }

            if (!create)
            {
                return EmptyValues;
            }

            values = new Dictionary<ResourceId, byte[]>();
            state.Positions.Add(position.Value, values);
            return values;
        }

        private static Dictionary<ResourceId, byte[]> EmptyValues { get; } = [];
    }

    private sealed record Session(
        string WorldDirectory,
        long Generation,
        Dictionary<string, OwnerState> Owners);

    private sealed class OwnerState
    {
        public Dictionary<ResourceId, byte[]> World { get; } = [];
        public Dictionary<PositionKey, Dictionary<ResourceId, byte[]>> Positions { get; } = [];
        public bool Dirty { get; set; }
    }

    private readonly record struct PositionKey(int X, int Y, int Z)
    {
        public static PositionKey From(ModBlockPosition position) => new(position.X, position.Y, position.Z);
    }

    private sealed class FileModel
    {
        public int FormatVersion { get; set; }
        public string? ModId { get; set; }
        public List<EntryModel>? World { get; set; }
        public List<PositionModel>? Positions { get; set; }
    }

    private sealed class PositionModel
    {
        public int X { get; set; }
        public int Y { get; set; }
        public int Z { get; set; }
        public List<EntryModel>? Values { get; set; }
    }

    private sealed class EntryModel
    {
        public string? Key { get; set; }
        public byte[]? Value { get; set; }
    }
}
