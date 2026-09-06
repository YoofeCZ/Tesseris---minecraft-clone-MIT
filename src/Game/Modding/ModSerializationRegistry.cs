using Tesseris.ModApi;

namespace Tesseris.Game.Modding;

public sealed class ModSerializationException : Exception
{
    public ModSerializationException(
        ResourceId serializerId,
        string ownerModId,
        string operation,
        Exception innerException)
        : base($"Mod '{ownerModId}' serializer '{serializerId}' failed during {operation}.", innerException)
    {
        SerializerId = serializerId;
        OwnerModId = ownerModId;
        Operation = operation;
    }

    public ResourceId SerializerId { get; }

    public string OwnerModId { get; }

    public string Operation { get; }
}

/// <summary>Frozen, thread-safe serializer and forward-migration registry for opaque mod values.</summary>
public sealed class ModSerializationRegistry
{
    private readonly int mainThreadId = Environment.CurrentManagedThreadId;
    private readonly Dictionary<ResourceId, Registration> registrations = [];
    private ModSerializerDescriptor[] descriptors = [];

    public bool IsFrozen { get; private set; }

    public IReadOnlyList<ModSerializerDescriptor> Registered => Array.AsReadOnly(
        IsFrozen
            ? descriptors
            : registrations.Values
                .OrderBy(registration => registration.Id.Value, StringComparer.Ordinal)
                .Select(registration => registration.Descriptor)
                .ToArray());

    internal IModSerializationRegistry ForMod(string modId)
    {
        EnsureMainThread();
        ArgumentException.ThrowIfNullOrWhiteSpace(modId);
        return new View(this, modId.Trim().ToLowerInvariant());
    }

    internal void Freeze()
    {
        EnsureMainThread();
        if (IsFrozen)
        {
            return;
        }

        foreach (Registration registration in registrations.Values)
        {
            registration.ValidateMigrations();
        }

        descriptors = registrations.Values
            .OrderBy(registration => registration.Id.Value, StringComparer.Ordinal)
            .Select(registration => registration.Descriptor)
            .ToArray();
        IsFrozen = true;
    }

    private void Register<T>(
        string owner,
        ResourceId id,
        int currentSchemaVersion,
        IModSerializer<T> serializer,
        IReadOnlyList<IModDataMigration> migrations)
    {
        EnsureMainThread();
        if (IsFrozen)
        {
            throw new InvalidOperationException("Mod serializer registration is frozen.");
        }

        ArgumentNullException.ThrowIfNull(serializer);
        ArgumentNullException.ThrowIfNull(migrations);
        if (!id.Value.StartsWith(owner + ":", StringComparison.Ordinal))
        {
            throw new ModHostException($"Mod '{owner}' may only register serializers in its own namespace.");
        }

        if (currentSchemaVersion < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(currentSchemaVersion), "Schema versions start at one.");
        }

        var registration = new Registration<T>(
            owner,
            id,
            currentSchemaVersion,
            serializer,
            migrations.ToArray());
        if (!registrations.TryAdd(id, registration))
        {
            throw new ModHostException($"Serializer '{id}' is already registered by '{registrations[id].Owner}'.");
        }
    }

    private ModSerializedValue Serialize<T>(ResourceId id, T value)
    {
        Registration<T> registration = Get<T>(id);
        try
        {
            ReadOnlyMemory<byte> payload = registration.Serializer.Serialize(value);
            return new ModSerializedValue(id, registration.CurrentSchemaVersion, payload.ToArray());
        }
        catch (Exception exception)
        {
            throw Failure(registration, "serialization", exception);
        }
    }

    private T Deserialize<T>(ModSerializedValue value)
    {
        Registration<T> registration = Get<T>(value.SerializerId);
        if (value.SchemaVersion < 1 || value.SchemaVersion > registration.CurrentSchemaVersion)
        {
            throw new ModSerializationException(
                registration.Id,
                registration.Owner,
                "deserialization",
                new InvalidDataException(
                    $"Value schema {value.SchemaVersion} is not supported; current schema is "
                    + $"{registration.CurrentSchemaVersion}."));
        }

        try
        {
            ReadOnlyMemory<byte> payload = value.Payload.ToArray();
            int version = value.SchemaVersion;
            while (version < registration.CurrentSchemaVersion)
            {
                IModDataMigration migration = registration.MigrationsByFrom[version];
                payload = migration.Migrate(payload).ToArray();
                version = migration.ToVersion;
            }

            return registration.Serializer.Deserialize(payload);
        }
        catch (Exception exception) when (exception is not ModSerializationException)
        {
            throw Failure(registration, "deserialization or migration", exception);
        }
    }

    private Registration<T> Get<T>(ResourceId id)
    {
        if (!IsFrozen)
        {
            throw new InvalidOperationException("Freeze serializers before using them.");
        }

        if (!registrations.TryGetValue(id, out Registration? untyped))
        {
            throw new KeyNotFoundException($"Serializer '{id}' is not registered.");
        }

        if (untyped is not Registration<T> typed)
        {
            throw new InvalidOperationException(
                $"Serializer '{id}' handles '{untyped.ValueType.FullName}', not '{typeof(T).FullName}'.");
        }

        return typed;
    }

    private static ModSerializationException Failure(Registration registration, string operation, Exception exception) =>
        new(registration.Id, registration.Owner, operation, exception);

    private void EnsureMainThread()
    {
        if (Environment.CurrentManagedThreadId != mainThreadId)
        {
            throw new InvalidOperationException("Mod serializers may only be registered on the game thread.");
        }
    }

    private abstract class Registration
    {
        protected Registration(
            string owner,
            ResourceId id,
            int currentSchemaVersion,
            Type valueType,
            IModDataMigration[] migrations)
        {
            Owner = owner;
            Id = id;
            CurrentSchemaVersion = currentSchemaVersion;
            ValueType = valueType;
            Descriptor = new ModSerializerDescriptor(id, currentSchemaVersion, valueType);
            MigrationsByFrom = migrations.ToDictionary(migration => migration.FromVersion);
        }

        public string Owner { get; }

        public ResourceId Id { get; }

        public int CurrentSchemaVersion { get; }

        public Type ValueType { get; }

        public ModSerializerDescriptor Descriptor { get; }

        public IReadOnlyDictionary<int, IModDataMigration> MigrationsByFrom { get; }

        public void ValidateMigrations()
        {
            foreach ((int from, IModDataMigration migration) in MigrationsByFrom)
            {
                if (from < 1 || migration.ToVersion <= from || migration.ToVersion > CurrentSchemaVersion)
                {
                    throw new ModHostException(
                        $"Mod '{Owner}' serializer '{Id}' has invalid migration "
                        + $"{migration.FromVersion}->{migration.ToVersion}.");
                }
            }

            if (MigrationsByFrom.Count == 0)
            {
                return;
            }

            int version = MigrationsByFrom.Keys.Min();
            var visited = new HashSet<int>();
            while (version < CurrentSchemaVersion)
            {
                if (!visited.Add(version) || !MigrationsByFrom.TryGetValue(version, out IModDataMigration? migration))
                {
                    throw new ModHostException(
                        $"Mod '{Owner}' serializer '{Id}' migration chain has a cycle or gap at schema {version}.");
                }

                version = migration.ToVersion;
            }

            if (version != CurrentSchemaVersion)
            {
                throw new ModHostException(
                    $"Mod '{Owner}' serializer '{Id}' migration chain does not end at schema {CurrentSchemaVersion}.");
            }
        }
    }

    private sealed class Registration<T> : Registration
    {
        public Registration(
            string owner,
            ResourceId id,
            int currentSchemaVersion,
            IModSerializer<T> serializer,
            IModDataMigration[] migrations)
            : base(owner, id, currentSchemaVersion, typeof(T), ValidateUnique(migrations)) =>
            Serializer = serializer;

        public IModSerializer<T> Serializer { get; }

        private static IModDataMigration[] ValidateUnique(IModDataMigration[] migrations)
        {
            foreach (IModDataMigration migration in migrations)
            {
                ArgumentNullException.ThrowIfNull(migration);
            }

            int? duplicate = migrations
                .GroupBy(migration => migration.FromVersion)
                .Where(group => group.Count() > 1)
                .Select(group => (int?)group.Key)
                .FirstOrDefault();
            if (duplicate is not null)
            {
                throw new ModHostException($"More than one migration starts at schema {duplicate.Value}.");
            }

            return migrations;
        }
    }

    private sealed class View(ModSerializationRegistry registry, string owner) : IModSerializationRegistry
    {
        public IReadOnlyList<ModSerializerDescriptor> Registered => registry.Registered;

        public void Register<T>(
            ResourceId id,
            int currentSchemaVersion,
            IModSerializer<T> serializer,
            IReadOnlyList<IModDataMigration> migrations) =>
            registry.Register(owner, id, currentSchemaVersion, serializer, migrations);

        public ModSerializedValue Serialize<T>(ResourceId serializerId, T value) =>
            registry.Serialize(serializerId, value);

        public T Deserialize<T>(ModSerializedValue value) => registry.Deserialize<T>(value);
    }
}
