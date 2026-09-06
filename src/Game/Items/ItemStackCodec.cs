using Tesseris.ModApi;

namespace Tesseris.Game.Items;

/// <summary>Length-delimited, stable-ID codec for item stacks and their opaque component payloads.</summary>
public static class ItemStackCodec
{
    private const int MaxStackBytes = 16 * 1024 * 1024;
    private const int MaxComponents = 1024;
    private const int MaxComponentBytes = 1024 * 1024;

    public static void Write(BinaryWriter writer, ItemStack stack, ItemRegistry items)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(items);

        if (stack.IsEmpty)
        {
            writer.Write(0);
            return;
        }

        using var stream = new MemoryStream();
        using (var payload = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            payload.Write(items.Definition(stack.Item).Id);
            payload.Write(stack.Count);
            payload.Write(stack.Damage);
            payload.Write(stack.Components.Count);
            foreach ((ResourceId id, ModSerializedValue value) in stack.Components.Values())
            {
                payload.Write(id.Value);
                payload.Write(value.SerializerId.Value);
                payload.Write(value.SchemaVersion);
                payload.Write(value.Payload.Length);
                payload.Write(value.Payload.Span);
            }
        }

        if (stream.Length > MaxStackBytes)
            throw new InvalidDataException($"Item stack payload is {stream.Length} bytes; limit is {MaxStackBytes}.");

        writer.Write((int)stream.Length);
        writer.Write(stream.GetBuffer(), 0, (int)stream.Length);
    }

    public static ItemStack Read(BinaryReader reader, ItemRegistry items)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(items);

        int length = reader.ReadInt32();
        if (length == 0) return ItemStack.Empty;
        if (length is < 0 or > MaxStackBytes)
            throw new InvalidDataException($"Item stack payload length {length} is outside 0..{MaxStackBytes}.");

        byte[] bytes = reader.ReadBytes(length);
        if (bytes.Length != length) throw new EndOfStreamException("Item stack payload is truncated.");

        using var stream = new MemoryStream(bytes, writable: false);
        using var payload = new BinaryReader(stream);
        string itemId = payload.ReadString();
        int count = payload.ReadInt32();
        int damage = payload.ReadInt32();
        int componentCount = payload.ReadInt32();
        if (componentCount is < 0 or > MaxComponents)
            throw new InvalidDataException($"Item stack contains {componentCount} components; limit is {MaxComponents}.");

        ItemComponentMap components = ItemComponentMap.Empty;
        for (int index = 0; index < componentCount; index++)
        {
            var componentId = new ResourceId(payload.ReadString());
            var serializerId = new ResourceId(payload.ReadString());
            int schemaVersion = payload.ReadInt32();
            int componentLength = payload.ReadInt32();
            if (componentLength is < 0 or > MaxComponentBytes)
                throw new InvalidDataException(
                    $"Item component '{componentId}' payload length {componentLength} is outside 0..{MaxComponentBytes}.");
            byte[] componentPayload = payload.ReadBytes(componentLength);
            if (componentPayload.Length != componentLength)
                throw new EndOfStreamException($"Item component '{componentId}' payload is truncated.");
            components = components.Set(
                componentId,
                new ModSerializedValue(serializerId, schemaVersion, componentPayload));
        }

        if (stream.Position != stream.Length)
            throw new InvalidDataException("Item stack payload contains trailing bytes.");

        int item = items.IndexOf(itemId);
        return item == ItemRegistry.Nothing || count <= 0
            ? ItemStack.Empty
            : new ItemStack(item, count, damage, components);
    }

    internal static ItemStack ReadLegacy(BinaryReader reader, ItemRegistry items)
    {
        string id = reader.ReadString();
        if (string.IsNullOrEmpty(id)) return ItemStack.Empty;

        int count = reader.ReadInt32();
        int damage = reader.ReadInt32();
        int item = items.IndexOf(id);
        return item == ItemRegistry.Nothing || count <= 0
            ? ItemStack.Empty
            : new ItemStack(item, count, damage);
    }
}
