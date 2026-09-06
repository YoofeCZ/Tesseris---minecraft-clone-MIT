using Tesseris.ModApi;

namespace Tesseris.Game.Items;

/// <summary>
/// Immutable, structurally comparable state attached to an item stack. Payloads are copied on input,
/// entries are kept in ordinal resource-ID order, and updates return a new map.
/// </summary>
public sealed class ItemComponentMap : IEquatable<ItemComponentMap>
{
    private readonly Entry[] entries;
    private readonly int hashCode;

    private ItemComponentMap(Entry[] entries)
    {
        this.entries = entries;
        hashCode = CalculateHashCode(entries);
    }

    public static ItemComponentMap Empty { get; } = new([]);

    public int Count => entries.Length;

    public IReadOnlyList<ResourceId> Keys => entries.Select(entry => entry.Id).ToArray();

    public bool TryGet(ResourceId id, out ModSerializedValue value)
    {
        int index = Find(id);
        if (index < 0)
        {
            value = default;
            return false;
        }

        Entry entry = entries[index];
        value = new ModSerializedValue(entry.SerializerId, entry.SchemaVersion, entry.Payload.ToArray());
        return true;
    }

    public ItemComponentMap Set(ResourceId id, ModSerializedValue value)
    {
        Validate(id, value);
        byte[] payload = value.Payload.ToArray();
        var replacement = new Entry(id, value.SerializerId, value.SchemaVersion, payload);
        int index = Find(id);

        if (index >= 0 && entries[index].Equals(replacement))
        {
            return this;
        }

        Entry[] updated;
        if (index >= 0)
        {
            updated = (Entry[])entries.Clone();
            updated[index] = replacement;
        }
        else
        {
            int insertion = ~index;
            updated = new Entry[entries.Length + 1];
            Array.Copy(entries, 0, updated, 0, insertion);
            updated[insertion] = replacement;
            Array.Copy(entries, insertion, updated, insertion + 1, entries.Length - insertion);
        }

        return new ItemComponentMap(updated);
    }

    public ItemComponentMap Remove(ResourceId id)
    {
        int index = Find(id);
        if (index < 0)
        {
            return this;
        }

        if (entries.Length == 1)
        {
            return Empty;
        }

        var updated = new Entry[entries.Length - 1];
        Array.Copy(entries, 0, updated, 0, index);
        Array.Copy(entries, index + 1, updated, index, entries.Length - index - 1);
        return new ItemComponentMap(updated);
    }

    internal IEnumerable<(ResourceId Id, ModSerializedValue Value)> Values()
    {
        foreach (Entry entry in entries)
        {
            yield return (
                entry.Id,
                new ModSerializedValue(entry.SerializerId, entry.SchemaVersion, entry.Payload.ToArray()));
        }
    }

    public bool Equals(ItemComponentMap? other)
    {
        if (ReferenceEquals(this, other)) return true;
        if (other is null || hashCode != other.hashCode || entries.Length != other.entries.Length) return false;

        for (int index = 0; index < entries.Length; index++)
        {
            if (!entries[index].Equals(other.entries[index])) return false;
        }

        return true;
    }

    public override bool Equals(object? obj) => obj is ItemComponentMap other && Equals(other);

    public override int GetHashCode() => hashCode;

    private int Find(ResourceId id) => Array.BinarySearch(
        entries,
        new Entry(id, default, 0, []),
        EntryIdComparer.Instance);

    private static void Validate(ResourceId id, ModSerializedValue value)
    {
        if (string.IsNullOrWhiteSpace(id.Value)) throw new ArgumentException("Component ID cannot be empty.", nameof(id));
        if (string.IsNullOrWhiteSpace(value.SerializerId.Value))
            throw new ArgumentException("Component serializer ID cannot be empty.", nameof(value));
        if (value.SchemaVersion < 0)
            throw new ArgumentOutOfRangeException(nameof(value), "Component schema version cannot be negative.");
    }

    private static int CalculateHashCode(IEnumerable<Entry> values)
    {
        var hash = new HashCode();
        foreach (Entry entry in values)
        {
            hash.Add(entry.Id);
            hash.Add(entry.SerializerId);
            hash.Add(entry.SchemaVersion);
            foreach (byte item in entry.Payload) hash.Add(item);
        }
        return hash.ToHashCode();
    }

    private readonly record struct Entry(
        ResourceId Id,
        ResourceId SerializerId,
        int SchemaVersion,
        byte[] Payload)
    {
        public bool Equals(Entry other) =>
            Id == other.Id
            && SerializerId == other.SerializerId
            && SchemaVersion == other.SchemaVersion
            && Payload.AsSpan().SequenceEqual(other.Payload);

        public override int GetHashCode() => HashCode.Combine(Id, SerializerId, SchemaVersion);
    }

    private sealed class EntryIdComparer : IComparer<Entry>
    {
        public static EntryIdComparer Instance { get; } = new();

        public int Compare(Entry x, Entry y) => string.CompareOrdinal(x.Id.Value, y.Id.Value);
    }
}
