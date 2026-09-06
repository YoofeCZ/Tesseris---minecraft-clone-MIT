using System.Text;
using Tesseris.ModApi;

namespace Tesseris.Game.Entities;

public sealed partial class EntityWorld
{
    private const uint FileMagic = 0x43455856; // VXEC little-endian
    private const int FileFormatVersion = 1;
    private const int MaxEntities = 1_000_000;
    private const int MaxComponentsPerEntity = 4096;
    private const int MaxPayloadBytes = 16 * 1024 * 1024;
    private const int MaxStringBytes = 4096;

    /// <summary>Writes a deterministic, ID-ordered snapshot without interpreting component payloads.</summary>
    public void Save(Stream destination)
    {
        EnsureSimulationThread();
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
        {
            throw new ArgumentException("The entity destination stream is not writable.", nameof(destination));
        }

        using var writer = new BinaryWriter(destination, Encoding.UTF8, leaveOpen: true);
        writer.Write(FileMagic);
        writer.Write(FileFormatVersion);
        writer.Write(WorldGeneration);
        writer.Write(worldSeed);
        writer.Write(nextEntityValue);
        writer.Write(entities.Count);
        foreach (EntityRecord entity in entities.Values)
        {
            writer.Write(entity.Id.Value);
            writer.Write(entity.Id.Generation);
            WriteResourceId(writer, entity.ArchetypeId);
            writer.Write(entity.Revision);
            writer.Write(entity.Components.Count);
            foreach ((ResourceId componentId, ModSerializedValue value) in entity.Components)
            {
                WriteResourceId(writer, componentId);
                WriteResourceId(writer, value.SerializerId);
                writer.Write(value.SchemaVersion);
                ReadOnlySpan<byte> payload = value.Payload.Span;
                writer.Write(payload.Length);
                writer.Write(payload);
            }
        }
    }

    /// <summary>
    /// Loads all well-formed component blobs, including unregistered IDs and future schema versions.
    /// Runtime mutation still requires a currently registered component descriptor.
    /// </summary>
    public static EntityWorld Load(EntityRegistry registry, Stream source)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead)
        {
            throw new ArgumentException("The entity source stream is not readable.", nameof(source));
        }

        try
        {
            using var reader = new BinaryReader(source, Encoding.UTF8, leaveOpen: true);
            if (reader.ReadUInt32() != FileMagic)
            {
                throw new InvalidDataException("The entity file has an invalid signature.");
            }

            int version = reader.ReadInt32();
            if (version != FileFormatVersion)
            {
                throw new InvalidDataException(
                    $"Entity file format {version} is not supported; expected {FileFormatVersion}.");
            }

            uint generation = reader.ReadUInt32();
            long seed = reader.ReadInt64();
            ulong next = reader.ReadUInt64();
            int count = ReadCount(reader, MaxEntities, "entity");
            var world = new EntityWorld(registry, seed, generation);
            ulong highest = 0;
            for (int index = 0; index < count; index++)
            {
                ulong value = reader.ReadUInt64();
                uint entityGeneration = reader.ReadUInt32();
                if (value == 0 || entityGeneration != generation)
                {
                    throw new InvalidDataException("Entity IDs must be nonzero and belong to the serialized world generation.");
                }

                var id = new ModEntityId(value, entityGeneration);
                ResourceId archetype = ReadResourceId(reader);
                uint revision = reader.ReadUInt32();
                int componentCount = ReadCount(reader, MaxComponentsPerEntity, "component");
                var components = new SortedDictionary<ResourceId, ModSerializedValue>(ResourceIdComparer.Instance);
                for (int componentIndex = 0; componentIndex < componentCount; componentIndex++)
                {
                    ResourceId componentId = ReadResourceId(reader);
                    ResourceId serializerId = ReadResourceId(reader);
                    int schemaVersion = reader.ReadInt32();
                    if (schemaVersion < 0)
                    {
                        throw new InvalidDataException("Entity component schema versions cannot be negative.");
                    }

                    int payloadLength = ReadCount(reader, MaxPayloadBytes, "component payload byte");
                    byte[] payload = reader.ReadBytes(payloadLength);
                    if (payload.Length != payloadLength)
                    {
                        throw new EndOfStreamException("The entity component payload is truncated.");
                    }

                    if (!components.TryAdd(
                            componentId,
                            new ModSerializedValue(serializerId, schemaVersion, payload)))
                    {
                        throw new InvalidDataException(
                            $"Entity {value}:{generation} contains duplicate component '{componentId}'.");
                    }
                }

                var entity = new EntityRecord(id, archetype, components, revision);
                if (!world.entities.TryAdd(value, entity))
                {
                    throw new InvalidDataException($"Entity ID {value}:{generation} occurs more than once.");
                }

                world.Index(entity);
                highest = Math.Max(highest, value);
            }

            if (next == 0 || next <= highest)
            {
                throw new InvalidDataException("The serialized next entity ID is not above all live IDs.");
            }

            world.nextEntityValue = next;
            return world;
        }
        catch (Exception exception) when (exception is EndOfStreamException or IOException or ArgumentException)
        {
            throw new InvalidDataException("The entity file is corrupt or truncated.", exception);
        }
    }

    public byte[] SaveToBytes()
    {
        using var stream = new MemoryStream();
        Save(stream);
        return stream.ToArray();
    }

    private static void WriteResourceId(BinaryWriter writer, ResourceId id)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(id.Value);
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static ResourceId ReadResourceId(BinaryReader reader)
    {
        int length = ReadCount(reader, MaxStringBytes, "resource ID byte");
        byte[] bytes = reader.ReadBytes(length);
        if (bytes.Length != length)
        {
            throw new EndOfStreamException("A resource ID is truncated.");
        }

        return new ResourceId(new UTF8Encoding(false, true).GetString(bytes));
    }

    private static int ReadCount(BinaryReader reader, int maximum, string description)
    {
        int count = reader.ReadInt32();
        if (count < 0 || count > maximum)
        {
            throw new InvalidDataException(
                $"The serialized {description} count {count} is outside 0..{maximum}.");
        }

        return count;
    }
}
