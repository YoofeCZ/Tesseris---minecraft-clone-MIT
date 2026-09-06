namespace Tesseris.ModApi;

/// <summary>
/// Immutable serialized value. The payload is an owned snapshot and must not alias a mutable engine buffer.
/// </summary>
public readonly record struct ModSerializedValue(
    ResourceId SerializerId,
    int SchemaVersion,
    ReadOnlyMemory<byte> Payload);

/// <summary>Pure serializer usable on any thread unless its registration explicitly says otherwise.</summary>
public interface IModSerializer<T>
{
    ReadOnlyMemory<byte> Serialize(T value);

    T Deserialize(ReadOnlyMemory<byte> payload);
}

/// <summary>
/// One deterministic forward-only migration. It must not access runtime world state and must produce the
/// same bytes for the same input. Throwing aborts the containing load transaction without partial commit.
/// </summary>
public interface IModDataMigration
{
    int FromVersion { get; }

    int ToVersion { get; }

    ReadOnlyMemory<byte> Migrate(ReadOnlyMemory<byte> payload);
}

public sealed record ModSerializerDescriptor(
    ResourceId Id,
    int CurrentSchemaVersion,
    Type ValueType);

/// <summary>
/// Configuration-time serializer registry. IDs must belong to the registering mod. Migration chains are
/// validated for gaps and cycles at freeze time. The descriptor snapshot is immutable and ID-ordered.
/// </summary>
public interface IModSerializationRegistry
{
    IReadOnlyList<ModSerializerDescriptor> Registered { get; }

    void Register<T>(
        ResourceId id,
        int currentSchemaVersion,
        IModSerializer<T> serializer,
        IReadOnlyList<IModDataMigration> migrations);

    ModSerializedValue Serialize<T>(ResourceId serializerId, T value);

    T Deserialize<T>(ModSerializedValue value);
}
