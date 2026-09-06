using System.Text;
using Tesseris.ModApi;

namespace Tesseris.Game.Items;

public sealed record RegisteredModContainerType(string ModId, ModContainerTypeDefinition Definition);
public sealed record RegisteredModMenu(string ModId, ModMenuDefinition Definition, IModMenuHandler Handler);

public sealed class ModMenuActionException : Exception
{
    public ModMenuActionException(
        string modId,
        ResourceId menuId,
        ResourceId actionId,
        Exception innerException)
        : base($"Mod '{modId}' menu '{menuId}' failed handling action '{actionId}'.", innerException)
    {
        ModId = modId;
        MenuId = menuId;
        ActionId = actionId;
    }

    public string ModId { get; }
    public ResourceId MenuId { get; }
    public ResourceId ActionId { get; }
}

/// <summary>
/// Generic world-scoped container and menu runtime. It contains no knowledge of furnaces, machines,
/// crafting or other gameplay-specific concepts.
/// </summary>
public sealed class ModContainerPlatform
{
    private const uint FileMagic = 0x54435856; // VXCT
    private const uint ActionMagic = 0x414D5856; // VXMA
    private const int FileVersion = 1;
    private const int ActionVersion = 1;
    private const int MaxContainers = 1_000_000;
    private const int MaxSlots = 65_536;
    private const int MaxComponents = 4096;
    private const int MaxActionBytes = 16 * 1024 * 1024;

    private readonly int mainThreadId = Environment.CurrentManagedThreadId;
    private readonly Dictionary<ResourceId, RegisteredModContainerType> types = [];
    private readonly Dictionary<ResourceId, RegisteredModMenu> menus = [];
    private readonly SortedDictionary<ModContainerId, ContainerRecord> containers = new(ContainerIdComparer.Instance);
    private readonly Dictionary<ModMenuSessionId, SessionRecord> sessions = [];
    private RegisteredModContainerType[] frozenTypes = [];
    private RegisteredModMenu[] frozenMenus = [];
    private ulong idHigh;
    private ulong nextContainerLow = 1;
    private ulong nextSessionLow = 1;

    public ModContainerPlatform(ulong idHigh = 1)
    {
        if (idHigh == 0) throw new ArgumentOutOfRangeException(nameof(idHigh));
        this.idHigh = idHigh;
    }

    public bool IsFrozen { get; private set; }

    public IReadOnlyList<RegisteredModContainerType> ContainerTypes => IsFrozen
        ? frozenTypes
        : Array.AsReadOnly(types.Values.OrderBy(value => value.Definition.Id.Value, StringComparer.Ordinal).ToArray());

    public IReadOnlyList<RegisteredModMenu> Menus => IsFrozen
        ? frozenMenus
        : Array.AsReadOnly(menus.Values.OrderBy(value => value.Definition.Id.Value, StringComparer.Ordinal).ToArray());

    internal IModContainerPlatform ForMod(string modId)
    {
        EnsureMainThread();
        ValidateModId(modId);
        return new OwnerView(this, modId);
    }

    public void Freeze()
    {
        EnsureMainThread();
        if (IsFrozen) return;
        foreach (RegisteredModMenu menu in menus.Values)
        {
            if (!types.ContainsKey(menu.Definition.ContainerTypeId))
                throw new InvalidOperationException(
                    $"Menu '{menu.Definition.Id}' references unknown container type '{menu.Definition.ContainerTypeId}'.");
        }

        frozenTypes = types.Values.OrderBy(value => value.Definition.Id.Value, StringComparer.Ordinal).ToArray();
        frozenMenus = menus.Values.OrderBy(value => value.Definition.Id.Value, StringComparer.Ordinal).ToArray();
        IsFrozen = true;
    }

    /// <summary>
    /// Creates a persistent container. Optional slot snapshots define insertion/extraction constraints;
    /// omitted slots default to empty and unrestricted.
    /// </summary>
    public ModContainerId Create(
        ResourceId typeId,
        IReadOnlyList<ModContainerSlotSnapshot>? initialSlots = null,
        IReadOnlyList<ModComponentValue>? initialState = null)
    {
        EnsureRuntime();
        if (!types.TryGetValue(typeId, out RegisteredModContainerType? type))
            throw new KeyNotFoundException($"Unknown container type '{typeId}'.");

        SlotRecord[] slots = CreateSlots(type.Definition, initialSlots);
        SortedDictionary<ResourceId, ModSerializedValue> state = CreateState(type, initialState ?? [], allowUnknown: false);
        ModContainerId id = AllocateContainerId();
        containers.Add(id, new ContainerRecord(id, typeId, 0, slots, state));
        return id;
    }

    public bool TryGetContainer(ModContainerId id, out ModContainerSnapshot? container)
    {
        EnsureMainThread();
        if (containers.TryGetValue(id, out ContainerRecord? record))
        {
            container = Snapshot(record);
            return true;
        }

        container = null;
        return false;
    }

    public bool TryGetSession(ModMenuSessionId id, out ModMenuSessionSnapshot? session)
    {
        EnsureMainThread();
        if (sessions.TryGetValue(id, out SessionRecord? record)
            && containers.TryGetValue(record.ContainerId, out ContainerRecord? container))
        {
            session = Snapshot(record, container);
            return true;
        }

        session = null;
        return false;
    }

    /// <summary>Creates a deferred host/mod transaction outside a menu callback.</summary>
    public IModContainerCommandBuffer CreateCommandBuffer(string ownerModId)
    {
        EnsureRuntime();
        ValidateModId(ownerModId);
        return new ContainerCommandBuffer(this, ownerModId);
    }

    public bool Commit(IModContainerCommandBuffer commands)
    {
        EnsureRuntime();
        ContainerCommandBuffer buffer = RequireBuffer(commands);
        return CommitCore(buffer, null, 0);
    }

    /// <summary>Handles both local UI actions and decoded network actions through one validation path.</summary>
    public ModActionResult HandleAction(ModMenuSessionId sessionId, ModMenuAction action)
    {
        EnsureRuntime();
        ArgumentNullException.ThrowIfNull(action);
        if (!sessions.TryGetValue(sessionId, out SessionRecord? session)
            || !session.IsOpen
            || session.Revision != action.ExpectedSessionRevision
            || !containers.TryGetValue(session.ContainerId, out ContainerRecord? container)
            || !menus.TryGetValue(session.MenuId, out RegisteredModMenu? menu))
        {
            return ModActionResult.Denied;
        }

        if (!OwnedBy(menu.ModId, action.Id) || !ValidValue(action.Payload)) return ModActionResult.Denied;
        ModMenuAction ownedAction = Clone(action);
        var buffer = new ContainerCommandBuffer(this, menu.ModId);
        ModActionResult result;
        try
        {
            result = menu.Handler.Handle(Snapshot(session, container), ownedAction, buffer);
            if (!Enum.IsDefined(result)) throw new InvalidOperationException($"Unknown action result '{result}'.");
        }
        catch (Exception exception)
        {
            buffer.Discard();
            throw new ModMenuActionException(menu.ModId, menu.Definition.Id, action.Id, exception);
        }

        if (result != ModActionResult.Handled)
        {
            buffer.Discard();
            return result;
        }

        return CommitCore(buffer, sessionId, action.ExpectedSessionRevision)
            ? ModActionResult.Handled
            : ModActionResult.Denied;
    }

    public ModActionResult HandleSerializedAction(ModMenuSessionId session, ReadOnlyMemory<byte> payload) =>
        HandleAction(session, DeserializeAction(payload));

    public static byte[] SerializeAction(ModMenuAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(ActionMagic);
            writer.Write(ActionVersion);
            ModStackPlatform.WriteResourceId(writer, action.Id);
            writer.Write(action.ExpectedSessionRevision);
            writer.Write(action.Payload.HasValue);
            if (action.Payload is { } value) ModStackPlatform.WriteValue(writer, value);
        }

        return stream.ToArray();
    }

    public static ModMenuAction DeserializeAction(ReadOnlyMemory<byte> payload)
    {
        if (payload.Length > MaxActionBytes) throw new InvalidDataException("The menu action is too large.");
        try
        {
            using var stream = new MemoryStream(payload.ToArray(), writable: false);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
            if (reader.ReadUInt32() != ActionMagic || reader.ReadInt32() != ActionVersion)
                throw new InvalidDataException("Invalid menu action envelope.");
            ResourceId id = ModStackPlatform.ReadResourceId(reader);
            uint revision = reader.ReadUInt32();
            ModSerializedValue? value = reader.ReadBoolean() ? ModStackPlatform.ReadValue(reader) : null;
            if (stream.Position != stream.Length) throw new InvalidDataException("The menu action has trailing data.");
            return new ModMenuAction(id, revision, value);
        }
        catch (Exception exception) when (exception is EndOfStreamException or IOException or ArgumentException)
        {
            throw new InvalidDataException("The menu action is corrupt or truncated.", exception);
        }
    }

    public bool CloseSession(ModMenuSessionId id, uint expectedRevision)
    {
        EnsureRuntime();
        if (!sessions.TryGetValue(id, out SessionRecord? session)
            || !session.IsOpen
            || session.Revision != expectedRevision
            || session.Revision == uint.MaxValue)
            return false;
        session.IsOpen = false;
        session.Revision++;
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
        writer.Write(nextContainerLow);
        writer.Write(nextSessionLow);
        writer.Write(containers.Count);
        foreach (ContainerRecord container in containers.Values) WriteContainer(writer, Snapshot(container));
    }

    /// <summary>Loads all opaque state values without requiring their owning mod to be present.</summary>
    public void Load(Stream source)
    {
        EnsureRuntime();
        ArgumentNullException.ThrowIfNull(source);
        if (containers.Count != 0 || sessions.Count != 0)
            throw new InvalidOperationException("Container data can only be loaded into an empty runtime.");
        if (!source.CanRead) throw new ArgumentException("The source is not readable.", nameof(source));
        try
        {
            using var reader = new BinaryReader(source, Encoding.UTF8, leaveOpen: true);
            if (reader.ReadUInt32() != FileMagic || reader.ReadInt32() != FileVersion)
                throw new InvalidDataException("Invalid container file header.");
            ulong loadedHigh = reader.ReadUInt64();
            ulong loadedNextContainer = reader.ReadUInt64();
            ulong loadedNextSession = reader.ReadUInt64();
            if (loadedHigh == 0 || loadedNextContainer == 0 || loadedNextSession == 0)
                throw new InvalidDataException("Invalid container allocator state.");
            int count = ReadCount(reader, MaxContainers, "container");
            var loaded = new SortedDictionary<ModContainerId, ContainerRecord>(ContainerIdComparer.Instance);
            ulong highest = 0;
            for (int index = 0; index < count; index++)
            {
                ModContainerSnapshot snapshot = ReadContainer(reader);
                if (snapshot.Id.High != loadedHigh || snapshot.Id.Low == 0)
                    throw new InvalidDataException("A container has an invalid ID.");
                if (!loaded.TryAdd(snapshot.Id, FromSnapshot(snapshot)))
                    throw new InvalidDataException($"Duplicate container ID {snapshot.Id}.");
                highest = Math.Max(highest, snapshot.Id.Low);
            }

            if (loadedNextContainer <= highest) throw new InvalidDataException("The next container ID is not above live IDs.");
            idHigh = loadedHigh;
            nextContainerLow = loadedNextContainer;
            nextSessionLow = loadedNextSession;
            foreach ((ModContainerId id, ContainerRecord value) in loaded) containers.Add(id, value);
        }
        catch (Exception exception) when (exception is EndOfStreamException or IOException or ArgumentException)
        {
            throw new InvalidDataException("The container file is corrupt or truncated.", exception);
        }
    }

    public byte[] SaveToBytes()
    {
        using var stream = new MemoryStream();
        Save(stream);
        return stream.ToArray();
    }

    private void RegisterType(string owner, ModContainerTypeDefinition definition)
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(definition);
        EnsureOwned(owner, definition.Id, "container type");
        if (definition.SlotCount < 0 || definition.SlotCount > MaxSlots)
            throw new ArgumentOutOfRangeException(nameof(definition), $"Slot count must be within 0..{MaxSlots}.");
        if (definition.StateSerializerId is { } serializer) EnsureValid(serializer, nameof(definition.StateSerializerId));
        if (!types.TryAdd(definition.Id, new RegisteredModContainerType(owner, definition)))
            throw new InvalidOperationException($"Container type '{definition.Id}' is already registered.");
    }

    private void RegisterMenu(string owner, ModMenuDefinition definition, IModMenuHandler handler)
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(handler);
        EnsureOwned(owner, definition.Id, "menu");
        EnsureValid(definition.ContainerTypeId, nameof(definition.ContainerTypeId));
        EnsureValid(definition.ScreenId, nameof(definition.ScreenId));
        if (!menus.TryAdd(definition.Id, new RegisteredModMenu(owner, definition, handler)))
            throw new InvalidOperationException($"Menu '{definition.Id}' is already registered.");
    }

    private bool TryOpen(ResourceId menuId, ModContainerId containerId, out ModMenuSessionSnapshot? snapshot)
    {
        EnsureRuntime();
        if (!menus.TryGetValue(menuId, out RegisteredModMenu? menu)
            || !containers.TryGetValue(containerId, out ContainerRecord? container)
            || container.TypeId != menu.Definition.ContainerTypeId)
        {
            snapshot = null;
            return false;
        }

        ModMenuSessionId id = AllocateSessionId();
        var session = new SessionRecord(id, menuId, containerId, 0, true);
        sessions.Add(id, session);
        snapshot = Snapshot(session, container);
        return true;
    }

    private bool CommitCore(ContainerCommandBuffer buffer, ModMenuSessionId? actionSession, uint actionRevision)
    {
        buffer.Consume();
        var workingContainers = new Dictionary<ModContainerId, ContainerRecord>();
        var workingSessions = new Dictionary<ModMenuSessionId, SessionRecord>();
        if (actionSession is { } active)
        {
            if (!sessions.TryGetValue(active, out SessionRecord? source)
                || !source.IsOpen || source.Revision != actionRevision || source.Revision == uint.MaxValue)
                return false;
            workingSessions.Add(active, source.Clone());
        }

        foreach (ContainerCommand command in buffer.Commands)
        {
            switch (command)
            {
                case ContainerMutation mutation:
                    if (!containers.TryGetValue(mutation.Container, out ContainerRecord? original)
                        || original.Revision != mutation.ExpectedRevision || original.Revision == uint.MaxValue)
                        return false;
                    if (!workingContainers.TryGetValue(mutation.Container, out ContainerRecord? container))
                    {
                        container = original.Clone();
                        workingContainers.Add(mutation.Container, container);
                    }

                    if (mutation is SetSlotCommand slot)
                    {
                        if (slot.Slot < 0 || slot.Slot >= container.Slots.Length) return false;
                        SlotRecord target = container.Slots[slot.Slot];
                        bool removes = target.Stack is not null && (slot.Stack is null || slot.Stack.Id != target.Stack.Id);
                        bool inserts = slot.Stack is not null && (target.Stack is null || slot.Stack.Id != target.Stack.Id);
                        if ((removes && !target.CanExtract) || (inserts && !target.CanInsert)) return false;
                        target.Stack = slot.Stack is null ? null : ModStackPlatform.CloneSnapshot(slot.Stack);
                    }
                    else if (mutation is SetStateCommand state)
                    {
                        if (!types.TryGetValue(container.TypeId, out RegisteredModContainerType? type)
                            || type.Definition.StateSerializerId is not { } serializer
                            || serializer != state.State.Value.SerializerId)
                            return false;
                        container.Components[state.State.ComponentId] = Clone(state.State.Value);
                    }
                    break;

                case CloseCommand close:
                    if (!sessions.TryGetValue(close.Session, out SessionRecord? originalSession)
                        || originalSession.Revision != close.ExpectedRevision || !originalSession.IsOpen
                        || originalSession.Revision == uint.MaxValue
                        || !menus.TryGetValue(originalSession.MenuId, out RegisteredModMenu? ownedMenu)
                        || ownedMenu.ModId != buffer.Owner)
                        return false;
                    if (!workingSessions.TryGetValue(close.Session, out SessionRecord? session))
                    {
                        session = originalSession.Clone();
                        workingSessions.Add(close.Session, session);
                    }
                    session.IsOpen = false;
                    break;
            }
        }

        foreach ((ModContainerId id, ContainerRecord container) in workingContainers)
        {
            container.Revision++;
            containers[id] = container;
        }

        foreach ((ModMenuSessionId id, SessionRecord session) in workingSessions)
        {
            session.Revision++;
            sessions[id] = session;
        }

        return true;
    }

    private ContainerCommandBuffer RequireBuffer(IModContainerCommandBuffer commands)
    {
        if (commands is not ContainerCommandBuffer buffer || !ReferenceEquals(buffer.Platform, this))
            throw new ArgumentException("The container command buffer belongs to another platform.", nameof(commands));
        return buffer;
    }

    private SlotRecord[] CreateSlots(ModContainerTypeDefinition type, IReadOnlyList<ModContainerSlotSnapshot>? initial)
    {
        if (initial is null)
            return Enumerable.Range(0, type.SlotCount).Select(index => new SlotRecord(index, null, true, true)).ToArray();
        if (initial.Count != type.SlotCount) throw new ArgumentException("Initial slot count does not match the container type.", nameof(initial));
        var result = new SlotRecord[type.SlotCount];
        foreach (ModContainerSlotSnapshot slot in initial)
        {
            if (slot.Index < 0 || slot.Index >= type.SlotCount || result[slot.Index] is not null)
                throw new ArgumentException("Initial slots must contain each index exactly once.", nameof(initial));
            result[slot.Index] = new SlotRecord(
                slot.Index,
                slot.Stack is null ? null : ModStackPlatform.CloneSnapshot(slot.Stack),
                slot.CanInsert,
                slot.CanExtract);
        }

        return result;
    }

    private SortedDictionary<ResourceId, ModSerializedValue> CreateState(
        RegisteredModContainerType type,
        IReadOnlyList<ModComponentValue> initial,
        bool allowUnknown)
    {
        var result = new SortedDictionary<ResourceId, ModSerializedValue>(ResourceIdComparer.Instance);
        foreach (ModComponentValue value in initial)
        {
            EnsureValid(value.ComponentId, nameof(value.ComponentId));
            if (!allowUnknown && type.Definition.StateSerializerId != value.Value.SerializerId)
                throw new InvalidOperationException($"Container state '{value.ComponentId}' uses the wrong serializer.");
            if (!ValidValue(value.Value)) throw new ArgumentException("Invalid container state value.", nameof(initial));
            if (!result.TryAdd(value.ComponentId, Clone(value.Value)))
                throw new ArgumentException($"Duplicate container state '{value.ComponentId}'.", nameof(initial));
        }

        return result;
    }

    private static ModContainerSnapshot Snapshot(ContainerRecord record)
    {
        ModContainerSlotSnapshot[] slots = record.Slots.Select(slot => new ModContainerSlotSnapshot(
            slot.Index,
            slot.Stack is null ? null : ModStackPlatform.CloneSnapshot(slot.Stack),
            slot.CanInsert,
            slot.CanExtract)).ToArray();
        ModComponentValue[] state = record.Components
            .Select(pair => new ModComponentValue(pair.Key, Clone(pair.Value)))
            .ToArray();
        return new ModContainerSnapshot(
            record.Id, record.TypeId, record.Revision, Array.AsReadOnly(slots), Array.AsReadOnly(state));
    }

    private static ModMenuSessionSnapshot Snapshot(SessionRecord session, ContainerRecord container) =>
        new(session.Id, session.MenuId, Snapshot(container), session.Revision, session.IsOpen);

    private static ModMenuAction Clone(ModMenuAction action) =>
        new(action.Id, action.ExpectedSessionRevision, action.Payload is { } value ? Clone(value) : null);

    private static ModSerializedValue Clone(ModSerializedValue value) =>
        new(value.SerializerId, value.SchemaVersion, value.Payload.ToArray());

    private static bool ValidValue(ModSerializedValue? value) => value is null || ValidValue(value.Value);
    private static bool ValidValue(ModSerializedValue value) =>
        !string.IsNullOrWhiteSpace(value.SerializerId.Value) && value.SchemaVersion >= 0 && value.Payload.Length <= MaxActionBytes;

    private ModContainerId AllocateContainerId()
    {
        if (nextContainerLow == 0 || nextContainerLow == ulong.MaxValue)
            throw new InvalidOperationException("Container ID space exhausted.");
        return new ModContainerId(idHigh, nextContainerLow++);
    }

    private ModMenuSessionId AllocateSessionId()
    {
        if (nextSessionLow == 0 || nextSessionLow == ulong.MaxValue)
            throw new InvalidOperationException("Menu session ID space exhausted.");
        return new ModMenuSessionId(idHigh, nextSessionLow++);
    }

    private static void WriteContainer(BinaryWriter writer, ModContainerSnapshot container)
    {
        writer.Write(container.Id.High);
        writer.Write(container.Id.Low);
        ModStackPlatform.WriteResourceId(writer, container.TypeId);
        writer.Write(container.Revision);
        writer.Write(container.Slots.Count);
        foreach (ModContainerSlotSnapshot slot in container.Slots.OrderBy(value => value.Index))
        {
            writer.Write(slot.Index);
            writer.Write(slot.CanInsert);
            writer.Write(slot.CanExtract);
            writer.Write(slot.Stack is not null);
            if (slot.Stack is not null) ModStackPlatform.WriteSnapshot(writer, slot.Stack);
        }

        writer.Write(container.Components.Count);
        foreach (ModComponentValue state in container.Components.OrderBy(value => value.ComponentId.Value, StringComparer.Ordinal))
        {
            ModStackPlatform.WriteResourceId(writer, state.ComponentId);
            ModStackPlatform.WriteValue(writer, state.Value);
        }
    }

    private static ModContainerSnapshot ReadContainer(BinaryReader reader)
    {
        var id = new ModContainerId(reader.ReadUInt64(), reader.ReadUInt64());
        ResourceId type = ModStackPlatform.ReadResourceId(reader);
        uint revision = reader.ReadUInt32();
        int slotCount = ReadCount(reader, MaxSlots, "slot");
        var slots = new ModContainerSlotSnapshot[slotCount];
        var seenSlots = new bool[slotCount];
        for (int index = 0; index < slotCount; index++)
        {
            int slotIndex = reader.ReadInt32();
            if (slotIndex < 0 || slotIndex >= slotCount || seenSlots[slotIndex])
                throw new InvalidDataException("Container slot indices are invalid or duplicated.");
            seenSlots[slotIndex] = true;
            bool canInsert = reader.ReadBoolean();
            bool canExtract = reader.ReadBoolean();
            ModItemStackSnapshot? stack = reader.ReadBoolean() ? ModStackPlatform.ReadSnapshot(reader) : null;
            slots[slotIndex] = new ModContainerSlotSnapshot(slotIndex, stack, canInsert, canExtract);
        }

        int componentCount = ReadCount(reader, MaxComponents, "container state");
        var state = new List<ModComponentValue>(componentCount);
        var seen = new HashSet<ResourceId>();
        for (int index = 0; index < componentCount; index++)
        {
            ResourceId componentId = ModStackPlatform.ReadResourceId(reader);
            if (!seen.Add(componentId)) throw new InvalidDataException($"Duplicate container state '{componentId}'.");
            state.Add(new ModComponentValue(componentId, ModStackPlatform.ReadValue(reader)));
        }

        return new ModContainerSnapshot(id, type, revision, slots, state);
    }

    private static ContainerRecord FromSnapshot(ModContainerSnapshot snapshot)
    {
        SlotRecord[] slots = snapshot.Slots.OrderBy(value => value.Index).Select(value => new SlotRecord(
            value.Index,
            value.Stack is null ? null : ModStackPlatform.CloneSnapshot(value.Stack),
            value.CanInsert,
            value.CanExtract)).ToArray();
        var state = new SortedDictionary<ResourceId, ModSerializedValue>(ResourceIdComparer.Instance);
        foreach (ModComponentValue value in snapshot.Components) state.Add(value.ComponentId, Clone(value.Value));
        return new ContainerRecord(snapshot.Id, snapshot.TypeId, snapshot.Revision, slots, state);
    }

    private static int ReadCount(BinaryReader reader, int maximum, string description)
    {
        int value = reader.ReadInt32();
        if (value < 0 || value > maximum) throw new InvalidDataException($"Invalid {description} count {value}.");
        return value;
    }

    private void EnsureMutable()
    {
        EnsureMainThread();
        if (IsFrozen) throw new InvalidOperationException("Container/menu registration is frozen.");
    }

    private void EnsureRuntime()
    {
        EnsureMainThread();
        if (!IsFrozen) throw new InvalidOperationException("Freeze container/menu registration before play.");
    }

    private void EnsureMainThread()
    {
        if (Environment.CurrentManagedThreadId != mainThreadId)
            throw new InvalidOperationException("Mod container operations must run on the main thread.");
    }

    private static void EnsureOwned(string owner, ResourceId id, string kind)
    {
        EnsureValid(id, nameof(id));
        if (!OwnedBy(owner, id))
            throw new InvalidOperationException($"Mod '{owner}' may only register {kind} IDs in its own namespace.");
    }

    private static bool OwnedBy(string owner, ResourceId id) =>
        !string.IsNullOrWhiteSpace(id.Value) && id.Value.StartsWith(owner + ":", StringComparison.Ordinal);

    private static void EnsureValid(ResourceId id, string parameter)
    {
        if (string.IsNullOrWhiteSpace(id.Value)) throw new ArgumentException("A non-default resource ID is required.", parameter);
    }

    private static void ValidateModId(string modId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modId);
        _ = new ResourceId(modId + ":validation");
    }

    private sealed class OwnerView(ModContainerPlatform platform, string modId) : IModContainerPlatform
    {
        public void RegisterContainerType(ModContainerTypeDefinition definition) => platform.RegisterType(modId, definition);
        public void RegisterMenu(ModMenuDefinition definition, IModMenuHandler handler) => platform.RegisterMenu(modId, definition, handler);
        public bool TryOpen(ResourceId menuId, ModContainerId container, out ModMenuSessionSnapshot? session) =>
            platform.TryOpen(menuId, container, out session);
        public bool TryGetSession(ModMenuSessionId id, out ModMenuSessionSnapshot? session) => platform.TryGetSession(id, out session);
    }

    private sealed class ContainerCommandBuffer : IModContainerCommandBuffer
    {
        private bool consumed;
        public ContainerCommandBuffer(ModContainerPlatform platform, string owner)
        {
            Platform = platform;
            Owner = owner;
        }

        public ModContainerPlatform Platform { get; }
        public string Owner { get; }
        public List<ContainerCommand> Commands { get; } = [];

        public void SetSlot(ModContainerId container, uint expectedRevision, int slot, ModItemStackSnapshot? stack)
        {
            EnsureOpen();
            if (slot < 0) throw new ArgumentOutOfRangeException(nameof(slot));
            Commands.Add(new SetSlotCommand(
                container, expectedRevision, slot,
                stack is null ? null : ModStackPlatform.CloneSnapshot(stack)));
        }

        public void SetState(ModContainerId container, uint expectedRevision, ModComponentValue state)
        {
            EnsureOpen();
            EnsureOwned(Owner, state.ComponentId, "container state mutation");
            if (!ValidValue(state.Value)) throw new ArgumentException("Invalid container state value.", nameof(state));
            Commands.Add(new SetStateCommand(
                container, expectedRevision,
                new ModComponentValue(state.ComponentId, Clone(state.Value))));
        }

        public void Close(ModMenuSessionId session, uint expectedRevision)
        {
            EnsureOpen();
            Commands.Add(new CloseCommand(session, expectedRevision));
        }

        public void Consume()
        {
            EnsureOpen();
            consumed = true;
        }

        public void Discard()
        {
            if (!consumed) consumed = true;
        }

        private void EnsureOpen()
        {
            Platform.EnsureMainThread();
            if (consumed) throw new InvalidOperationException("A container command buffer is single-use.");
        }
    }

    private sealed class ContainerRecord(
        ModContainerId id,
        ResourceId typeId,
        uint revision,
        SlotRecord[] slots,
        SortedDictionary<ResourceId, ModSerializedValue> components)
    {
        public ModContainerId Id { get; } = id;
        public ResourceId TypeId { get; } = typeId;
        public uint Revision { get; set; } = revision;
        public SlotRecord[] Slots { get; } = slots;
        public SortedDictionary<ResourceId, ModSerializedValue> Components { get; } = components;

        public ContainerRecord Clone()
        {
            SlotRecord[] slotCopy = Slots.Select(value => value.Clone()).ToArray();
            var componentCopy = new SortedDictionary<ResourceId, ModSerializedValue>(ResourceIdComparer.Instance);
            foreach ((ResourceId id, ModSerializedValue value) in Components) componentCopy.Add(id, ModContainerPlatform.Clone(value));
            return new ContainerRecord(Id, TypeId, Revision, slotCopy, componentCopy);
        }
    }

    private sealed class SlotRecord(int index, ModItemStackSnapshot? stack, bool canInsert, bool canExtract)
    {
        public int Index { get; } = index;
        public ModItemStackSnapshot? Stack { get; set; } = stack;
        public bool CanInsert { get; } = canInsert;
        public bool CanExtract { get; } = canExtract;
        public SlotRecord Clone() => new(Index, Stack is null ? null : ModStackPlatform.CloneSnapshot(Stack), CanInsert, CanExtract);
    }

    private sealed class SessionRecord(
        ModMenuSessionId id,
        ResourceId menuId,
        ModContainerId containerId,
        uint revision,
        bool isOpen)
    {
        public ModMenuSessionId Id { get; } = id;
        public ResourceId MenuId { get; } = menuId;
        public ModContainerId ContainerId { get; } = containerId;
        public uint Revision { get; set; } = revision;
        public bool IsOpen { get; set; } = isOpen;
        public SessionRecord Clone() => new(Id, MenuId, ContainerId, Revision, IsOpen);
    }

    private abstract record ContainerCommand;
    private abstract record ContainerMutation(ModContainerId Container, uint ExpectedRevision) : ContainerCommand;
    private sealed record SetSlotCommand(
        ModContainerId Container, uint ExpectedRevision, int Slot, ModItemStackSnapshot? Stack)
        : ContainerMutation(Container, ExpectedRevision);
    private sealed record SetStateCommand(
        ModContainerId Container, uint ExpectedRevision, ModComponentValue State)
        : ContainerMutation(Container, ExpectedRevision);
    private sealed record CloseCommand(ModMenuSessionId Session, uint ExpectedRevision) : ContainerCommand;

    private sealed class ResourceIdComparer : IComparer<ResourceId>
    {
        public static readonly ResourceIdComparer Instance = new();
        public int Compare(ResourceId left, ResourceId right) => StringComparer.Ordinal.Compare(left.Value, right.Value);
    }

    private sealed class ContainerIdComparer : IComparer<ModContainerId>
    {
        public static readonly ContainerIdComparer Instance = new();
        public int Compare(ModContainerId left, ModContainerId right)
        {
            int high = left.High.CompareTo(right.High);
            return high != 0 ? high : left.Low.CompareTo(right.Low);
        }
    }
}
