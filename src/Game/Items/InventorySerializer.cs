namespace Tesseris.Game.Items;

/// <summary>Versioned player-inventory codec using stable item IDs and <see cref="ItemStackCodec"/>.</summary>
public static class InventorySerializer
{
    private const uint Magic = 0x31564E49; // "INV1"

    public static byte[] Encode(Inventory inventory, ItemRegistry items)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(items);

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(Magic);
        writer.Write(inventory.Selected);
        writer.Write(Inventory.AllSlots);
        ItemStackCodec.Write(writer, inventory.Held, items);
        for (int slot = 0; slot < Inventory.AllSlots; slot++)
            ItemStackCodec.Write(writer, inventory[slot], items);
        writer.Flush();
        return stream.ToArray();
    }

    public static Inventory Decode(ReadOnlySpan<byte> data, ItemRegistry items)
    {
        ArgumentNullException.ThrowIfNull(items);
        using var stream = new MemoryStream(data.ToArray(), writable: false);
        using var reader = new BinaryReader(stream);
        if (reader.ReadUInt32() != Magic) throw new InvalidDataException("Inventory has an unknown format.");

        int selected = reader.ReadInt32();
        int slotCount = reader.ReadInt32();
        if (slotCount != Inventory.AllSlots)
            throw new InvalidDataException($"Inventory contains {slotCount} slots; expected {Inventory.AllSlots}.");

        var inventory = new Inventory(items)
        {
            Held = ItemStackCodec.Read(reader, items),
        };
        inventory.Select(selected);
        for (int slot = 0; slot < Inventory.AllSlots; slot++)
            inventory[slot] = ItemStackCodec.Read(reader, items);

        if (stream.Position != stream.Length) throw new InvalidDataException("Inventory contains trailing bytes.");
        return inventory;
    }

    public static void Save(string path, Inventory inventory, ItemRegistry items)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        string temporary = fullPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            byte[] bytes = Encode(inventory, items);
            using (var stream = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static bool TryLoad(string path, ItemRegistry items, out Inventory? inventory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(items);
        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            inventory = null;
            return false;
        }

        inventory = Decode(File.ReadAllBytes(fullPath), items);
        return true;
    }
}
