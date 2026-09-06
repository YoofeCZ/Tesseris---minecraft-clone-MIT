using System.Text;
using Tesseris.ModApi;

namespace Tesseris.Game.Items;

public sealed record RegisteredStackComponent(string ModId, ModStackComponentDescriptor Descriptor);

/// <summary>
/// World-scoped item-stack runtime. Mods see immutable snapshots and deferred command buffers;
/// host code owns creation, persistence and transaction commits.
/// </summary>
public sealed class ModStackPlatform
{
    private const uint FileMagic = 0x54535856; // VXST
    private const int FileVersion = 1;
    private const int MaxStacks = 1_000_000;
    private const int MaxComponents = 4096;
    private const int MaxPayloadBytes = 16 * 1024 * 1024;
    private const int MaxStringBytes = 4096;

    private readonly int mainThreadId = Environment.CurrentManagedThreadId;
    private readonly Dictionary<ResourceId, RegisteredStackComponent> components = [];
    private readonly SortedDictionary<ModStackId, StackRecord> stacks = new(StackIdComparer.Instance);
    private RegisteredStackComponent[] frozenComponents = [];
    private ulong idHigh;
    private ulong nextIdLow = 1;

    public ModStackPlatform(ulong idHigh = 1)
    {
        if (idHigh == 0) throw new ArgumentOutOfRangeException(nameof(idHigh));
        this.idHigh = idHigh;
    }

    public bool IsFrozen { get; private set; }

    public IReadOnlyList<RegisteredStackComponent> Components => IsFrozen
        ? frozenComponents
        : Array.AsReadOnly(components.Values
            .OrderBy(value => value.Descriptor.Id.Value, StringComparer.Ordinal)
            .ToArray());

    internal IModStackPlatform ForMod(string modId)
    {
        EnsureMainThread();
        ValidateModId(modId);
        return new OwnerView(this, modId);
    }

    public void Freeze()
    {
        EnsureMainThread();
        if (IsFrozen) return;
        frozenComponents = components.Values
            .OrderBy(value => value.Descriptor.Id.Value, StringComparer.Ordinal)
            .ToArray();
        IsFrozen = true;
    }

    /// <summary>Creates a host-owned logical stack and returns its stable world-scoped ID.</summary>
    public ModStackId Create(
        ResourceId itemId,
        int count,
        IReadOnlyList<ModStackComponentValue>? initialComponents = null)
    {
        EnsureRuntime();
        EnsureValid(itemId, nameof(itemId));
        if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count));
        SortedDictionary<ResourceId, ModSerializedValue> values = ValidateInitial(initialComponents ?? []);
        ModStackId id = AllocateId();
        stacks.Add(id, new StackRecord(id, itemId, count, 0, values));
        return id;
    }

    public bool TryGet(ModStackId id, out ModItemStackSnapshot? stack)
    {
        EnsureMainThread();
        if (stacks.TryGetValue(id, out StackRecord? record))
        {
            stack = Snapshot(record);
            return true;
        }

        stack = null;
        return false;
    }

    /// <summary>Atomically applies one deferred buffer. Any stale/missing target rejects every command.</summary>
    public bool Commit(IModStackCommandBuffer commands)
    {
        EnsureRuntime();
        if (commands is not StackCommandBuffer buffer || !ReferenceEquals(buffer.Platform, this))
        {
            throw new ArgumentException("The stack command buffer belongs to another platform.", nameof(commands));
        }

        buffer.Consume();
        var working = new Dictionary<ModStackId, StackRecord>();
        foreach (StackCommand command in buffer.Commands)
        {
            if (!stacks.TryGetValue(command.Stack, out StackRecord? original)
                || original.Revision != command.ExpectedRevision
                || original.Revision == uint.MaxValue)
            {
                return false;
            }

            if (!working.TryGetValue(command.Stack, out StackRecord? record))
            {
                record = original.Clone();
                working.Add(command.Stack, record);
            }

            switch (command)
            {
                case SetComponentCommand set:
                    record.Components[set.Component.ComponentId] = Clone(set.Component.Value);
                    break;
                case RemoveComponentCommand remove:
                    record.Components.Remove(remove.ComponentId);
                    break;
                case SetCountCommand setCount:
                    record.Count = setCount.Count;
                    break;
            }
        }

        foreach ((ModStackId id, StackRecord record) in working)
        {
            record.Revision++;
            stacks[id] = record;
        }

        return true;
    }

    /// <summary>Splits a stack immediately for host inventory integration.</summary>
    public bool TrySplit(
        ModStackId sourceId,
        uint expectedRevision,
        int count,
        out ModStackId splitId)
    {
        EnsureRuntime();
        splitId = default;
        if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count));
        if (!stacks.TryGetValue(sourceId, out StackRecord? source)
            || source.Revision != expectedRevision
            || source.Revision == uint.MaxValue
            || count >= source.Count)
        {
            return false;
        }

        var splitComponents = new SortedDictionary<ResourceId, ModSerializedValue>(ResourceIdComparer.Instance);
        foreach ((ResourceId id, ModSerializedValue value) in source.Components)
        {
            // Unknown values are conservatively copied so a missing mod cannot destroy its data.
            if (!components.TryGetValue(id, out RegisteredStackComponent? registration)
                || registration.Descriptor.CopyWhenSplit)
            {
                splitComponents.Add(id, Clone(value));
            }
        }

        source.Count -= count;
        source.Revision++;
        splitId = AllocateId();
        stacks.Add(splitId, new StackRecord(splitId, source.ItemId, count, 0, splitComponents));
        return true;
    }

    /// <summary>Copies a complete immutable stack value to a new stable ID.</summary>
    public bool TryCopy(ModStackId sourceId, uint expectedRevision, out ModStackId copyId)
    {
        EnsureRuntime();
        copyId = default;
        if (!stacks.TryGetValue(sourceId, out StackRecord? source) || source.Revision != expectedRevision)
        {
            return false;
        }

        copyId = AllocateId();
        StackRecord copy = source.Clone(copyId);
        copy.Revision = 0;
        stacks.Add(copyId, copy);
        return true;
    }

    /// <summary>
    /// Moves as many items as possible into the target. Components marked RequireEqualToMerge and all
    /// unknown components must have byte-identical values on both stacks.
    /// </summary>
    public bool TryMerge(
        ModStackId targetId,
        uint expectedTargetRevision,
        ModStackId sourceId,
        uint expectedSourceRevision,
        int maximumCount,
        out int moved)
    {
        EnsureRuntime();
        moved = 0;
        if (maximumCount <= 0) throw new ArgumentOutOfRangeException(nameof(maximumCount));
        if (targetId == sourceId) return false;
        if (!stacks.TryGetValue(targetId, out StackRecord? target)
            || !stacks.TryGetValue(sourceId, out StackRecord? source)
            || target.Revision != expectedTargetRevision
            || source.Revision != expectedSourceRevision
            || target.Revision == uint.MaxValue
            || source.Revision == uint.MaxValue
            || target.ItemId != source.ItemId
            || target.Count >= maximumCount
            || !CanMerge(target, source))
        {
            return false;
        }

        moved = Math.Min(source.Count, maximumCount - target.Count);
        if (moved <= 0) return false;
        target.Count += moved;
        target.Revision++;
        source.Count -= moved;
        if (source.Count == 0)
        {
            stacks.Remove(sourceId);
        }
        else
        {
            source.Revision++;
        }

        return true;
    }

    public void Save(Stream destination)
    {
        EnsureRuntime();
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite) throw new ArgumentException("The destination is not writable.", nameof(destination));
        using var writer = new BinaryWriter(destination, Encoding.UTF8, leaveOpen: true);
        writer.Write(FileMagic);
        writer.Write(FileVersion);
        writer.Write(idHigh);
        writer.Write(nextIdLow);
        writer.Write(stacks.Count);
        foreach (StackRecord stack in stacks.Values) WriteSnapshot(writer, Snapshot(stack));
    }

    /// <summary>Atomically replaces an empty runtime with deterministic persisted stack data.</summary>
    public void Load(Stream source)
    {
        EnsureRuntime();
        ArgumentNullException.ThrowIfNull(source);
        if (stacks.Count != 0) throw new InvalidOperationException("Stack data can only be loaded into an empty runtime.");
        if (!source.CanRead) throw new ArgumentException("The source is not readable.", nameof(source));
        try
        {
            using var reader = new BinaryReader(source, Encoding.UTF8, leaveOpen: true);
            if (reader.ReadUInt32() != FileMagic) throw new InvalidDataException("Invalid stack file signature.");
            if (reader.ReadInt32() != FileVersion) throw new InvalidDataException("Unsupported stack file version.");
            ulong loadedHigh = reader.ReadUInt64();
            ulong loadedNext = reader.ReadUInt64();
            if (loadedHigh == 0 || loadedNext == 0) throw new InvalidDataException("Invalid stack ID allocator state.");
            int count = ReadCount(reader, MaxStacks, "stack");
            var loaded = new SortedDictionary<ModStackId, StackRecord>(StackIdComparer.Instance);
            ulong highest = 0;
            for (int index = 0; index < count; index++)
            {
                ModItemStackSnapshot snapshot = ReadSnapshot(reader);
                if (snapshot.Id.High != loadedHigh || snapshot.Id.Low == 0 || snapshot.Count <= 0)
                    throw new InvalidDataException("A persisted stack has an invalid ID or count.");
                if (!loaded.TryAdd(snapshot.Id, FromSnapshot(snapshot)))
                    throw new InvalidDataException($"Duplicate stack ID {snapshot.Id}.");
                highest = Math.Max(highest, snapshot.Id.Low);
            }

            if (loadedNext <= highest) throw new InvalidDataException("The next stack ID is not above live IDs.");
            idHigh = loadedHigh;
            nextIdLow = loadedNext;
            foreach ((ModStackId id, StackRecord value) in loaded) stacks.Add(id, value);
        }
        catch (Exception exception) when (exception is EndOfStreamException or IOException or ArgumentException)
        {
            throw new InvalidDataException("The stack file is corrupt or truncated.", exception);
        }
    }

    public byte[] SaveToBytes()
    {
        using var stream = new MemoryStream();
        Save(stream);
        return stream.ToArray();
    }

    internal static ModItemStackSnapshot CloneSnapshot(ModItemStackSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        EnsureValid(snapshot.ItemId, nameof(snapshot.ItemId));
        if (snapshot.Id.High == 0 || snapshot.Id.Low == 0 || snapshot.Count <= 0)
            throw new ArgumentException("A stack snapshot must have a valid ID and positive count.", nameof(snapshot));
        ModStackComponentValue[] values = snapshot.Components
            .Select(Clone)
            .OrderBy(value => value.ComponentId.Value, StringComparer.Ordinal)
            .ToArray();
        if (values.Select(value => value.ComponentId).Distinct().Count() != values.Length)
            throw new ArgumentException("A stack snapshot contains duplicate components.", nameof(snapshot));
        return new ModItemStackSnapshot(
            snapshot.Id, snapshot.ItemId, snapshot.Count, snapshot.Revision, Array.AsReadOnly(values));
    }

    internal static void WriteSnapshot(BinaryWriter writer, ModItemStackSnapshot stack)
    {
        writer.Write(stack.Id.High);
        writer.Write(stack.Id.Low);
        WriteResourceId(writer, stack.ItemId);
        writer.Write(stack.Count);
        writer.Write(stack.Revision);
        writer.Write(stack.Components.Count);
        foreach (ModStackComponentValue component in stack.Components.OrderBy(value => value.ComponentId.Value, StringComparer.Ordinal))
        {
            WriteResourceId(writer, component.ComponentId);
            WriteValue(writer, component.Value);
        }
    }

    internal static ModItemStackSnapshot ReadSnapshot(BinaryReader reader)
    {
        var id = new ModStackId(reader.ReadUInt64(), reader.ReadUInt64());
        ResourceId item = ReadResourceId(reader);
        int count = reader.ReadInt32();
        uint revision = reader.ReadUInt32();
        int componentCount = ReadCount(reader, MaxComponents, "stack component");
        var values = new List<ModStackComponentValue>(componentCount);
        var seen = new HashSet<ResourceId>();
        for (int index = 0; index < componentCount; index++)
        {
            ResourceId componentId = ReadResourceId(reader);
            if (!seen.Add(componentId)) throw new InvalidDataException($"Duplicate stack component '{componentId}'.");
            values.Add(new ModStackComponentValue(componentId, ReadValue(reader)));
        }

        return CloneSnapshot(new ModItemStackSnapshot(id, item, count, revision, values));
    }

    internal static void WriteValue(BinaryWriter writer, ModSerializedValue value)
    {
        WriteResourceId(writer, value.SerializerId);
        writer.Write(value.SchemaVersion);
        writer.Write(value.Payload.Length);
        writer.Write(value.Payload.Span);
    }

    internal static ModSerializedValue ReadValue(BinaryReader reader)
    {
        ResourceId serializer = ReadResourceId(reader);
        int schemaVersion = reader.ReadInt32();
        if (schemaVersion < 0) throw new InvalidDataException("Negative schema version.");
        int length = ReadCount(reader, MaxPayloadBytes, "payload byte");
        byte[] payload = reader.ReadBytes(length);
        if (payload.Length != length) throw new EndOfStreamException("A serialized value is truncated.");
        return new ModSerializedValue(serializer, schemaVersion, payload);
    }

    internal static void WriteResourceId(BinaryWriter writer, ResourceId id)
    {
        EnsureValid(id, nameof(id));
        byte[] bytes = Encoding.UTF8.GetBytes(id.Value);
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    internal static ResourceId ReadResourceId(BinaryReader reader)
    {
        int length = ReadCount(reader, MaxStringBytes, "resource ID byte");
        byte[] bytes = reader.ReadBytes(length);
        if (bytes.Length != length) throw new EndOfStreamException("A resource ID is truncated.");
        return new ResourceId(new UTF8Encoding(false, true).GetString(bytes));
    }

    private void Register(string modId, ModStackComponentDescriptor descriptor)
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(descriptor);
        EnsureOwned(modId, descriptor.Id, "stack component");
        EnsureValid(descriptor.SerializerId, nameof(descriptor.SerializerId));
        if (!components.TryAdd(descriptor.Id, new RegisteredStackComponent(modId, descriptor)))
            throw new InvalidOperationException($"Stack component '{descriptor.Id}' is already registered.");
    }

    private IModStackCommandBuffer CreateBuffer(string modId)
    {
        EnsureRuntime();
        return new StackCommandBuffer(this, modId);
    }

    private SortedDictionary<ResourceId, ModSerializedValue> ValidateInitial(IReadOnlyList<ModStackComponentValue> initial)
    {
        ArgumentNullException.ThrowIfNull(initial);
        var result = new SortedDictionary<ResourceId, ModSerializedValue>(ResourceIdComparer.Instance);
        foreach (ModStackComponentValue component in initial)
        {
            ValidateComponent(component, requireRegistered: true);
            if (!result.TryAdd(component.ComponentId, Clone(component.Value)))
                throw new ArgumentException($"Duplicate stack component '{component.ComponentId}'.", nameof(initial));
        }

        return result;
    }

    private void ValidateComponent(ModStackComponentValue component, bool requireRegistered)
    {
        EnsureValid(component.ComponentId, nameof(component.ComponentId));
        EnsureValid(component.Value.SerializerId, nameof(component.Value.SerializerId));
        if (component.Value.SchemaVersion < 0) throw new ArgumentOutOfRangeException(nameof(component));
        if (!components.TryGetValue(component.ComponentId, out RegisteredStackComponent? registered))
        {
            if (requireRegistered) throw new KeyNotFoundException($"Unknown stack component '{component.ComponentId}'.");
            return;
        }

        if (registered.Descriptor.SerializerId != component.Value.SerializerId)
            throw new InvalidOperationException(
                $"Stack component '{component.ComponentId}' uses serializer '{component.Value.SerializerId}', "
                + $"expected '{registered.Descriptor.SerializerId}'.");
    }

    private bool CanMerge(StackRecord left, StackRecord right)
    {
        foreach (ResourceId id in left.Components.Keys.Union(right.Components.Keys))
        {
            bool requireEqual = !components.TryGetValue(id, out RegisteredStackComponent? registration)
                                || registration.Descriptor.RequireEqualToMerge;
            if (!requireEqual) continue;
            if (!left.Components.TryGetValue(id, out ModSerializedValue leftValue)
                || !right.Components.TryGetValue(id, out ModSerializedValue rightValue)
                || !ValueEquals(leftValue, rightValue))
                return false;
        }

        return true;
    }

    private ModStackId AllocateId()
    {
        if (nextIdLow == 0 || nextIdLow == ulong.MaxValue) throw new InvalidOperationException("Stack ID space exhausted.");
        return new ModStackId(idHigh, nextIdLow++);
    }

    private static StackRecord FromSnapshot(ModItemStackSnapshot snapshot)
    {
        var values = new SortedDictionary<ResourceId, ModSerializedValue>(ResourceIdComparer.Instance);
        foreach (ModStackComponentValue component in snapshot.Components) values.Add(component.ComponentId, Clone(component.Value));
        return new StackRecord(snapshot.Id, snapshot.ItemId, snapshot.Count, snapshot.Revision, values);
    }

    private static ModItemStackSnapshot Snapshot(StackRecord record)
    {
        ModStackComponentValue[] values = record.Components
            .Select(pair => new ModStackComponentValue(pair.Key, Clone(pair.Value)))
            .ToArray();
        return new ModItemStackSnapshot(
            record.Id, record.ItemId, record.Count, record.Revision, Array.AsReadOnly(values));
    }

    private static ModStackComponentValue Clone(ModStackComponentValue value) =>
        new(value.ComponentId, Clone(value.Value));

    private static ModSerializedValue Clone(ModSerializedValue value) =>
        new(value.SerializerId, value.SchemaVersion, value.Payload.ToArray());

    private static bool ValueEquals(ModSerializedValue left, ModSerializedValue right) =>
        left.SerializerId == right.SerializerId
        && left.SchemaVersion == right.SchemaVersion
        && left.Payload.Span.SequenceEqual(right.Payload.Span);

    private void EnsureMutable()
    {
        EnsureMainThread();
        if (IsFrozen) throw new InvalidOperationException("Stack component registration is frozen.");
    }

    private void EnsureRuntime()
    {
        EnsureMainThread();
        if (!IsFrozen) throw new InvalidOperationException("Freeze stack component registration before play.");
    }

    private void EnsureMainThread()
    {
        if (Environment.CurrentManagedThreadId != mainThreadId)
            throw new InvalidOperationException("Mod stack operations must run on the main thread.");
    }

    private static void EnsureOwned(string owner, ResourceId id, string kind)
    {
        EnsureValid(id, nameof(id));
        if (!id.Value.StartsWith(owner + ":", StringComparison.Ordinal))
            throw new InvalidOperationException($"Mod '{owner}' may only register {kind} IDs in its own namespace.");
    }

    private static void ValidateModId(string modId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modId);
        _ = new ResourceId(modId + ":validation");
    }

    private static void EnsureValid(ResourceId id, string parameter)
    {
        if (string.IsNullOrWhiteSpace(id.Value)) throw new ArgumentException("A non-default resource ID is required.", parameter);
    }

    private static int ReadCount(BinaryReader reader, int maximum, string description)
    {
        int value = reader.ReadInt32();
        if (value < 0 || value > maximum) throw new InvalidDataException($"Invalid {description} count {value}.");
        return value;
    }

    private sealed class OwnerView(ModStackPlatform platform, string modId) : IModStackPlatform
    {
        public void RegisterComponent(ModStackComponentDescriptor descriptor) => platform.Register(modId, descriptor);
        public bool TryGet(ModStackId id, out ModItemStackSnapshot? stack) => platform.TryGet(id, out stack);
        public IModStackCommandBuffer CreateCommandBuffer() => platform.CreateBuffer(modId);
    }

    private sealed class StackCommandBuffer : IModStackCommandBuffer
    {
        private readonly string modId;
        private bool consumed;
        public StackCommandBuffer(ModStackPlatform platform, string modId)
        {
            Platform = platform;
            this.modId = modId;
        }

        public ModStackPlatform Platform { get; }
        public List<StackCommand> Commands { get; } = [];

        public void SetComponent(ModStackId stack, uint expectedRevision, ModStackComponentValue component)
        {
            EnsureOpen();
            EnsureOwned(modId, component.ComponentId, "stack component mutation");
            Platform.ValidateComponent(component, requireRegistered: true);
            Commands.Add(new SetComponentCommand(stack, expectedRevision, Clone(component)));
        }

        public void RemoveComponent(ModStackId stack, uint expectedRevision, ResourceId componentId)
        {
            EnsureOpen();
            EnsureOwned(modId, componentId, "stack component mutation");
            if (!Platform.components.ContainsKey(componentId)) throw new KeyNotFoundException($"Unknown stack component '{componentId}'.");
            Commands.Add(new RemoveComponentCommand(stack, expectedRevision, componentId));
        }

        public void SetCount(ModStackId stack, uint expectedRevision, int count)
        {
            EnsureOpen();
            if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count));
            Commands.Add(new SetCountCommand(stack, expectedRevision, count));
        }

        public void Consume()
        {
            EnsureOpen();
            consumed = true;
        }

        private void EnsureOpen()
        {
            Platform.EnsureMainThread();
            if (consumed) throw new InvalidOperationException("A stack command buffer is single-use.");
        }
    }

    private sealed class StackRecord(
        ModStackId id,
        ResourceId itemId,
        int count,
        uint revision,
        SortedDictionary<ResourceId, ModSerializedValue> components)
    {
        public ModStackId Id { get; } = id;
        public ResourceId ItemId { get; } = itemId;
        public int Count { get; set; } = count;
        public uint Revision { get; set; } = revision;
        public SortedDictionary<ResourceId, ModSerializedValue> Components { get; } = components;

        public StackRecord Clone() => Clone(Id);
        public StackRecord Clone(ModStackId newId)
        {
            var copy = new SortedDictionary<ResourceId, ModSerializedValue>(ResourceIdComparer.Instance);
            foreach ((ResourceId key, ModSerializedValue value) in Components) copy.Add(key, ModStackPlatform.Clone(value));
            return new StackRecord(newId, ItemId, Count, Revision, copy);
        }
    }

    private abstract record StackCommand(ModStackId Stack, uint ExpectedRevision);
    private sealed record SetComponentCommand(ModStackId Stack, uint ExpectedRevision, ModStackComponentValue Component)
        : StackCommand(Stack, ExpectedRevision);
    private sealed record RemoveComponentCommand(ModStackId Stack, uint ExpectedRevision, ResourceId ComponentId)
        : StackCommand(Stack, ExpectedRevision);
    private sealed record SetCountCommand(ModStackId Stack, uint ExpectedRevision, int Count)
        : StackCommand(Stack, ExpectedRevision);

    private sealed class ResourceIdComparer : IComparer<ResourceId>
    {
        public static readonly ResourceIdComparer Instance = new();
        public int Compare(ResourceId left, ResourceId right) => StringComparer.Ordinal.Compare(left.Value, right.Value);
    }

    private sealed class StackIdComparer : IComparer<ModStackId>
    {
        public static readonly StackIdComparer Instance = new();
        public int Compare(ModStackId left, ModStackId right)
        {
            int high = left.High.CompareTo(right.High);
            return high != 0 ? high : left.Low.CompareTo(right.Low);
        }
    }
}
