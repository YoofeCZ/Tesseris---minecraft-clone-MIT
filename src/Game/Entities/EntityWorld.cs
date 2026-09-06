using System.Collections;
using System.Text;
using Tesseris.ModApi;

namespace Tesseris.Game.Entities;

public readonly record struct EntityPosition(double X, double Y, double Z);

public readonly record struct EntityBounds(EntityPosition Min, EntityPosition Max);

/// <summary>Optional richer context implemented by the host without expanding the stable v2 contract.</summary>
public interface IEntitySystemRuntimeContext : IModSystemContext
{
    ResourceId SystemId { get; }

    IDeterministicRandom Random { get; }
}

/// <summary>Identifies a failing system and the last entity snapshot it accessed, when known.</summary>
public sealed class ModEntitySystemException : Exception
{
    public ModEntitySystemException(
        string modId,
        ResourceId systemId,
        ModSystemPhase phase,
        ModEntityId? entityId,
        Exception innerException)
        : base(
            entityId is { } entity
                ? $"Mod '{modId}' entity system '{systemId}' failed during {phase} at entity {entity.Value}:{entity.Generation}."
                : $"Mod '{modId}' entity system '{systemId}' failed during {phase}.",
            innerException)
    {
        ModId = modId;
        SystemId = systemId;
        Phase = phase;
        EntityId = entityId;
    }

    public string ModId { get; }

    public ResourceId SystemId { get; }

    public ModSystemPhase Phase { get; }

    public ModEntityId? EntityId { get; }
}

/// <summary>
/// World-scoped deterministic ECS-lite runtime. Mod systems only receive immutable snapshots and
/// deferred command buffers; direct methods are intended for host bootstrap and integration.
/// </summary>
public sealed partial class EntityWorld
{
    private readonly EntityRegistry registry;
    private readonly int simulationThreadId = Environment.CurrentManagedThreadId;
    private readonly SortedDictionary<ulong, EntityRecord> entities = [];
    private readonly Dictionary<ResourceId, SortedSet<ulong>> componentIndex = [];
    private readonly long worldSeed;
    private ulong nextEntityValue = 1;
    private SpatialIndex? spatial;

    public EntityWorld(EntityRegistry registry, long worldSeed, uint worldGeneration = 1)
    {
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
        if (!registry.IsFrozen)
        {
            throw new InvalidOperationException("Freeze the entity registry before creating a world.");
        }

        if (worldGeneration == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(worldGeneration));
        }

        this.worldSeed = worldSeed;
        WorldGeneration = worldGeneration;
    }

    public uint WorldGeneration { get; }

    public int Count
    {
        get
        {
            EnsureSimulationThread();
            return entities.Count;
        }
    }

    /// <summary>Host-side immediate creation outside a running system phase.</summary>
    public ModEntityId Create(ResourceId archetypeId)
    {
        EnsureSimulationThread();
        return CreateCore(archetypeId);
    }

    public bool Destroy(ModEntityId entity, uint expectedRevision)
    {
        EnsureSimulationThread();
        return DestroyCore(entity, expectedRevision);
    }

    public bool SetComponent(ModEntityId entity, uint expectedRevision, ModComponentValue component)
    {
        EnsureSimulationThread();
        registry.ValidateComponentValue(component, new ResourceId("tesseris:runtime_entity"));
        return SetComponentCore(entity, expectedRevision, EntityRegistry.Clone(component));
    }

    public bool RemoveComponent(ModEntityId entity, uint expectedRevision, ResourceId componentId)
    {
        EnsureSimulationThread();
        if (!registry.TryGetComponent(componentId, out _))
        {
            throw new KeyNotFoundException($"Unknown entity component '{componentId}'.");
        }

        return RemoveComponentCore(entity, expectedRevision, componentId);
    }

    public bool TryGet(ModEntityId entity, out ModEntitySnapshot? snapshot)
    {
        EnsureSimulationThread();
        if (TryRecord(entity, out EntityRecord? record))
        {
            snapshot = Snapshot(record!);
            return true;
        }

        snapshot = null;
        return false;
    }

    public IReadOnlyList<ModEntitySnapshot> WithAll(params ResourceId[] requiredComponents) =>
        WithAll((IReadOnlyList<ResourceId>)requiredComponents);

    public IReadOnlyList<ModEntitySnapshot> WithAll(IReadOnlyList<ResourceId> requiredComponents)
    {
        EnsureSimulationThread();
        ArgumentNullException.ThrowIfNull(requiredComponents);
        return Array.AsReadOnly(QueryRecords(requiredComponents).Select(Snapshot).ToArray());
    }

    /// <summary>
    /// Configures one serialized component as the spatial position source. Rebuilding here is a
    /// one-time integration operation; subsequent queries use cells and never require a world scan.
    /// </summary>
    public void ConfigureSpatialIndex(
        ResourceId positionComponentId,
        Func<ModSerializedValue, EntityPosition?> projector,
        double cellSize = 16d)
    {
        EnsureSimulationThread();
        ArgumentNullException.ThrowIfNull(projector);
        if (!registry.TryGetComponent(positionComponentId, out _))
        {
            throw new KeyNotFoundException($"Unknown spatial component '{positionComponentId}'.");
        }

        if (!double.IsFinite(cellSize) || cellSize <= 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(cellSize));
        }

        spatial = new SpatialIndex(positionComponentId, projector, cellSize);
        foreach (EntityRecord entity in entities.Values)
        {
            spatial.Refresh(entity);
        }
    }

    public IReadOnlyList<ModEntitySnapshot> QuerySpatial(
        EntityBounds bounds,
        IReadOnlyList<ResourceId>? requiredComponents = null)
    {
        EnsureSimulationThread();
        if (spatial is null)
        {
            throw new InvalidOperationException("No spatial entity component is configured.");
        }

        ValidateBounds(bounds);
        ResourceId[] required = requiredComponents?.Distinct().ToArray() ?? [];
        ulong[] ids = spatial.Query(bounds);
        var result = new List<ModEntitySnapshot>(ids.Length);
        foreach (ulong id in ids)
        {
            if (entities.TryGetValue(id, out EntityRecord? entity) && HasAll(entity, required))
            {
                result.Add(Snapshot(entity));
            }
        }

        return Array.AsReadOnly(result.ToArray());
    }

    /// <summary>Executes all registered phases and commits each successful phase atomically.</summary>
    public void RunSystems(ulong tick, TimeSpan delta)
    {
        EnsureSimulationThread();
        if (delta < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(delta));
        }

        RegisteredEntitySystem[] systems = registry.Systems.ToArray();
        int offset = 0;
        while (offset < systems.Length)
        {
            ModSystemPhase phase = systems[offset].Phase;
            int end = offset + 1;
            while (end < systems.Length && systems[end].Phase == phase)
            {
                end++;
            }

            var buffers = new List<EntityCommandBuffer>(end - offset);
            for (int index = offset; index < end; index++)
            {
                RegisteredEntitySystem registration = systems[index];
                var context = new SystemContext(this, registration, tick, delta);
                try
                {
                    registration.System.Execute(context);
                }
                catch (Exception exception)
                {
                    throw new ModEntitySystemException(
                        registration.ModId,
                        registration.Id,
                        registration.Phase,
                        context.LastEntity,
                        exception);
                }

                buffers.Add(context.Buffer);
            }

            foreach (EntityCommandBuffer buffer in buffers)
            {
                buffer.Commit(this);
            }

            offset = end;
        }
    }

    private ModEntityId CreateCore(ResourceId archetypeId)
    {
        if (!registry.TryGetArchetype(archetypeId, out RegisteredEntityArchetype? registration))
        {
            throw new KeyNotFoundException($"Unknown entity archetype '{archetypeId}'.");
        }

        ulong value = nextEntityValue;
        if (value == 0 || value == ulong.MaxValue)
        {
            throw new InvalidOperationException("The entity ID space is exhausted.");
        }

        nextEntityValue++;
        var id = new ModEntityId(value, WorldGeneration);
        var components = new SortedDictionary<ResourceId, ModSerializedValue>(ResourceIdComparer.Instance);
        foreach (ModComponentValue component in registration!.Definition.InitialComponents)
        {
            ModComponentValue copy = EntityRegistry.Clone(component);
            components.Add(copy.ComponentId, copy.Value);
        }

        var entity = new EntityRecord(id, archetypeId, components, revision: 0);
        entities.Add(value, entity);
        Index(entity);
        spatial?.Refresh(entity);
        return id;
    }

    private bool DestroyCore(ModEntityId id, uint expectedRevision)
    {
        if (!TryRecord(id, out EntityRecord? entity) || entity!.Revision != expectedRevision)
        {
            return false;
        }

        Unindex(entity);
        spatial?.Remove(entity.Id.Value);
        entities.Remove(entity.Id.Value);
        return true;
    }

    private bool SetComponentCore(ModEntityId id, uint expectedRevision, ModComponentValue component)
    {
        if (!TryRecord(id, out EntityRecord? entity) || entity!.Revision != expectedRevision)
        {
            return false;
        }

        bool added = !entity.Components.ContainsKey(component.ComponentId);
        entity.Components[component.ComponentId] = component.Value;
        entity.Revision = checked(entity.Revision + 1);
        if (added)
        {
            AddToIndex(component.ComponentId, entity.Id.Value);
        }

        spatial?.Refresh(entity);
        return true;
    }

    private bool RemoveComponentCore(ModEntityId id, uint expectedRevision, ResourceId componentId)
    {
        if (!TryRecord(id, out EntityRecord? entity) || entity!.Revision != expectedRevision)
        {
            return false;
        }

        if (!entity.Components.Remove(componentId))
        {
            return true;
        }

        entity.Revision = checked(entity.Revision + 1);
        RemoveFromIndex(componentId, entity.Id.Value);
        spatial?.Refresh(entity);
        return true;
    }

    private IEnumerable<EntityRecord> QueryRecords(IReadOnlyList<ResourceId> requiredComponents)
    {
        ResourceId[] required = requiredComponents.Distinct().ToArray();
        if (required.Length == 0)
        {
            return entities.Values;
        }

        SortedSet<ulong>? smallest = null;
        foreach (ResourceId component in required)
        {
            if (!componentIndex.TryGetValue(component, out SortedSet<ulong>? candidates))
            {
                return [];
            }

            if (smallest is null || candidates.Count < smallest.Count)
            {
                smallest = candidates;
            }
        }

        return smallest!.Where(id => entities.TryGetValue(id, out EntityRecord? entity) && HasAll(entity, required))
            .Select(id => entities[id]);
    }

    private static bool HasAll(EntityRecord entity, IReadOnlyList<ResourceId> required)
    {
        foreach (ResourceId id in required)
        {
            if (!entity.Components.ContainsKey(id))
            {
                return false;
            }
        }

        return true;
    }

    private bool TryRecord(ModEntityId id, out EntityRecord? record)
    {
        if (id.Generation == WorldGeneration && entities.TryGetValue(id.Value, out EntityRecord? found)
            && found.Id == id)
        {
            record = found;
            return true;
        }

        record = null;
        return false;
    }

    private static ModEntitySnapshot Snapshot(EntityRecord entity)
    {
        ModComponentValue[] components = entity.Components
            .Select(pair => EntityRegistry.Clone(new ModComponentValue(pair.Key, pair.Value)))
            .ToArray();
        return new ModEntitySnapshot(
            entity.Id,
            entity.ArchetypeId,
            Array.AsReadOnly(components),
            entity.Revision);
    }

    private void Index(EntityRecord entity)
    {
        foreach (ResourceId component in entity.Components.Keys)
        {
            AddToIndex(component, entity.Id.Value);
        }
    }

    private void Unindex(EntityRecord entity)
    {
        foreach (ResourceId component in entity.Components.Keys)
        {
            RemoveFromIndex(component, entity.Id.Value);
        }
    }

    private void AddToIndex(ResourceId component, ulong entity)
    {
        if (!componentIndex.TryGetValue(component, out SortedSet<ulong>? values))
        {
            values = [];
            componentIndex.Add(component, values);
        }

        values.Add(entity);
    }

    private void RemoveFromIndex(ResourceId component, ulong entity)
    {
        if (!componentIndex.TryGetValue(component, out SortedSet<ulong>? values))
        {
            return;
        }

        values.Remove(entity);
        if (values.Count == 0)
        {
            componentIndex.Remove(component);
        }
    }

    private void EnsureSimulationThread()
    {
        if (Environment.CurrentManagedThreadId != simulationThreadId)
        {
            throw new InvalidOperationException("Entity simulation and mutation must run on the simulation thread.");
        }
    }

    private static void ValidateBounds(EntityBounds bounds)
    {
        if (!Finite(bounds.Min) || !Finite(bounds.Max)
            || bounds.Min.X > bounds.Max.X
            || bounds.Min.Y > bounds.Max.Y
            || bounds.Min.Z > bounds.Max.Z)
        {
            throw new ArgumentException("Spatial bounds must be finite and ordered.", nameof(bounds));
        }
    }

    private static bool Finite(EntityPosition value) =>
        double.IsFinite(value.X) && double.IsFinite(value.Y) && double.IsFinite(value.Z);

    private sealed class EntityRecord
    {
        public EntityRecord(
            ModEntityId id,
            ResourceId archetypeId,
            SortedDictionary<ResourceId, ModSerializedValue> components,
            uint revision)
        {
            Id = id;
            ArchetypeId = archetypeId;
            Components = components;
            Revision = revision;
        }

        public ModEntityId Id { get; }
        public ResourceId ArchetypeId { get; }
        public SortedDictionary<ResourceId, ModSerializedValue> Components { get; }
        public uint Revision { get; set; }
    }

    private sealed class SystemContext : IEntitySystemRuntimeContext
    {
        private readonly EntityWorld world;
        private ModEntityId? lastEntity;

        public SystemContext(EntityWorld world, RegisteredEntitySystem registration, ulong tick, TimeSpan delta)
        {
            this.world = world;
            Registration = registration;
            Tick = tick;
            Delta = delta;
            Buffer = new EntityCommandBuffer(world.registry, registration, Track);
            Entities = new Query(this);
            Random = new EntityDeterministicRandom(EntityStableHash.ForSystem(
                world.worldSeed, tick, registration.Phase, registration.Id));
        }

        public RegisteredEntitySystem Registration { get; }
        public ResourceId SystemId => Registration.Id;
        public ulong Tick { get; }
        public TimeSpan Delta { get; }
        public IModEntityQuery Entities { get; }
        public IModEntityCommandBuffer Commands => Buffer;
        public IDeterministicRandom Random { get; }
        public EntityCommandBuffer Buffer { get; }
        public ModEntityId? LastEntity => lastEntity;

        public void Track(ModEntityId entity) => lastEntity = entity;

        private sealed class Query : IModEntityQuery
        {
            private readonly SystemContext context;
            public Query(SystemContext context) => this.context = context;

            public IReadOnlyList<ModEntitySnapshot> WithAll(IReadOnlyList<ResourceId> requiredComponents)
            {
                ArgumentNullException.ThrowIfNull(requiredComponents);
                ModEntitySnapshot[] snapshots = context.world.QueryRecords(requiredComponents)
                    .Select(Snapshot)
                    .ToArray();
                return new TrackingList(snapshots, context.Track);
            }

            public bool TryGet(ModEntityId entity, out ModEntitySnapshot? snapshot)
            {
                context.Track(entity);
                if (context.world.TryRecord(entity, out EntityRecord? record))
                {
                    snapshot = Snapshot(record!);
                    return true;
                }

                snapshot = null;
                return false;
            }
        }
    }

    private sealed class TrackingList : IReadOnlyList<ModEntitySnapshot>
    {
        private readonly ModEntitySnapshot[] values;
        private readonly Action<ModEntityId> track;

        public TrackingList(ModEntitySnapshot[] values, Action<ModEntityId> track)
        {
            this.values = values;
            this.track = track;
        }

        public int Count => values.Length;

        public ModEntitySnapshot this[int index]
        {
            get
            {
                ModEntitySnapshot value = values[index];
                track(value.Id);
                return value;
            }
        }

        public IEnumerator<ModEntitySnapshot> GetEnumerator()
        {
            foreach (ModEntitySnapshot value in values)
            {
                track(value.Id);
                yield return value;
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class EntityCommandBuffer : IModEntityCommandBuffer
    {
        private readonly EntityRegistry registry;
        private readonly RegisteredEntitySystem registration;
        private readonly Action<ModEntityId> track;
        private readonly List<Command> commands = [];
        private readonly HashSet<ResourceId> requests = [];

        public EntityCommandBuffer(
            EntityRegistry registry,
            RegisteredEntitySystem registration,
            Action<ModEntityId> track)
        {
            this.registry = registry;
            this.registration = registration;
            this.track = track;
        }

        public void Create(ResourceId archetypeId, ResourceId requestId)
        {
            EnsureRequestOwned(requestId);
            if (!registry.TryGetArchetype(archetypeId, out _))
            {
                throw new KeyNotFoundException($"Unknown entity archetype '{archetypeId}'.");
            }

            if (!requests.Add(requestId))
            {
                throw new InvalidOperationException(
                    $"Entity create request '{requestId}' is duplicated in system '{registration.Id}'.");
            }

            commands.Add(new CreateCommand(archetypeId, requestId));
        }

        public void Destroy(ModEntityId entity, uint expectedRevision)
        {
            track(entity);
            commands.Add(new DestroyCommand(entity, expectedRevision));
        }

        public void SetComponent(ModEntityId entity, uint expectedRevision, ModComponentValue component)
        {
            track(entity);
            registry.ValidateComponentValue(component, registration.Id);
            commands.Add(new SetComponentCommand(entity, expectedRevision, EntityRegistry.Clone(component)));
        }

        public void RemoveComponent(ModEntityId entity, uint expectedRevision, ResourceId componentId)
        {
            track(entity);
            if (!registry.TryGetComponent(componentId, out _))
            {
                throw new KeyNotFoundException($"Unknown entity component '{componentId}'.");
            }

            commands.Add(new RemoveComponentCommand(entity, expectedRevision, componentId));
        }

        public void Commit(EntityWorld world)
        {
            foreach (Command command in commands)
            {
                switch (command)
                {
                    case CreateCommand create:
                        world.CreateCore(create.ArchetypeId);
                        break;
                    case DestroyCommand destroy:
                        world.DestroyCore(destroy.Entity, destroy.ExpectedRevision);
                        break;
                    case SetComponentCommand set:
                        world.SetComponentCore(set.Entity, set.ExpectedRevision, set.Component);
                        break;
                    case RemoveComponentCommand remove:
                        world.RemoveComponentCore(remove.Entity, remove.ExpectedRevision, remove.ComponentId);
                        break;
                }
            }
        }

        private void EnsureRequestOwned(ResourceId requestId)
        {
            int separator = requestId.Value.IndexOf(':');
            if (!requestId.Value.AsSpan(0, separator).Equals(registration.ModId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Mod '{registration.ModId}' may only issue entity request IDs in its own namespace.");
            }
        }
    }

    private abstract record Command;
    private sealed record CreateCommand(ResourceId ArchetypeId, ResourceId RequestId) : Command;
    private sealed record DestroyCommand(ModEntityId Entity, uint ExpectedRevision) : Command;
    private sealed record SetComponentCommand(
        ModEntityId Entity,
        uint ExpectedRevision,
        ModComponentValue Component) : Command;
    private sealed record RemoveComponentCommand(
        ModEntityId Entity,
        uint ExpectedRevision,
        ResourceId ComponentId) : Command;

    private sealed class SpatialIndex
    {
        private const int MaxQueryCells = 262_144;
        private readonly ResourceId componentId;
        private readonly Func<ModSerializedValue, EntityPosition?> projector;
        private readonly double cellSize;
        private readonly Dictionary<Cell, SortedSet<ulong>> cells = [];
        private readonly Dictionary<ulong, (Cell Cell, EntityPosition Position)> positions = [];

        public SpatialIndex(
            ResourceId componentId,
            Func<ModSerializedValue, EntityPosition?> projector,
            double cellSize)
        {
            this.componentId = componentId;
            this.projector = projector;
            this.cellSize = cellSize;
        }

        public void Refresh(EntityRecord entity)
        {
            Remove(entity.Id.Value);
            if (!entity.Components.TryGetValue(componentId, out ModSerializedValue value))
            {
                return;
            }

            EntityPosition? projected = projector(value);
            if (projected is not { } position || !Finite(position))
            {
                return;
            }

            Cell cell = CellOf(position);
            if (!cells.TryGetValue(cell, out SortedSet<ulong>? ids))
            {
                ids = [];
                cells.Add(cell, ids);
            }

            ids.Add(entity.Id.Value);
            positions.Add(entity.Id.Value, (cell, position));
        }

        public void Remove(ulong entity)
        {
            if (!positions.Remove(entity, out (Cell Cell, EntityPosition Position) indexed))
            {
                return;
            }

            SortedSet<ulong> ids = cells[indexed.Cell];
            ids.Remove(entity);
            if (ids.Count == 0)
            {
                cells.Remove(indexed.Cell);
            }
        }

        public ulong[] Query(EntityBounds bounds)
        {
            Cell min = CellOf(bounds.Min);
            Cell max = CellOf(bounds.Max);
            decimal width = (decimal)max.X - min.X + 1;
            decimal height = (decimal)max.Y - min.Y + 1;
            decimal depth = (decimal)max.Z - min.Z + 1;
            if (width > MaxQueryCells || height > MaxQueryCells || depth > MaxQueryCells
                || width * height > MaxQueryCells
                || width * height * depth > MaxQueryCells)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(bounds),
                    $"A spatial query may touch at most {MaxQueryCells} index cells.");
            }

            var found = new SortedSet<ulong>();
            for (long x = min.X; x <= max.X; x++)
            {
                for (long y = min.Y; y <= max.Y; y++)
                {
                    for (long z = min.Z; z <= max.Z; z++)
                    {
                        if (!cells.TryGetValue(new Cell(x, y, z), out SortedSet<ulong>? ids))
                        {
                            continue;
                        }

                        foreach (ulong id in ids)
                        {
                            EntityPosition position = positions[id].Position;
                            if (Inside(position, bounds))
                            {
                                found.Add(id);
                            }
                        }
                    }
                }
            }

            return found.ToArray();
        }

        private Cell CellOf(EntityPosition position) => new(
            (long)Math.Floor(position.X / cellSize),
            (long)Math.Floor(position.Y / cellSize),
            (long)Math.Floor(position.Z / cellSize));

        private static bool Inside(EntityPosition value, EntityBounds bounds) =>
            value.X >= bounds.Min.X && value.X <= bounds.Max.X
            && value.Y >= bounds.Min.Y && value.Y <= bounds.Max.Y
            && value.Z >= bounds.Min.Z && value.Z <= bounds.Max.Z;

        private readonly record struct Cell(long X, long Y, long Z);
    }

    private sealed class ResourceIdComparer : IComparer<ResourceId>
    {
        public static readonly ResourceIdComparer Instance = new();
        public int Compare(ResourceId left, ResourceId right) =>
            StringComparer.Ordinal.Compare(left.Value, right.Value);
    }
}

/// <summary>Small deterministic stream whose result depends only on its seed and call sequence.</summary>
public sealed class EntityDeterministicRandom : IDeterministicRandom
{
    private const ulong Increment = 0x9E3779B97F4A7C15UL;
    private readonly ulong streamSeed;
    private ulong state;

    public EntityDeterministicRandom(ulong seed)
    {
        streamSeed = seed;
        state = seed;
    }

    public ulong NextUInt64()
    {
        state += Increment;
        return EntityStableHash.Mix(state);
    }

    public int NextInt(int exclusiveMax)
    {
        if (exclusiveMax <= 0) throw new ArgumentOutOfRangeException(nameof(exclusiveMax));
        return (int)NextBounded((ulong)exclusiveMax);
    }

    public int NextInt(int inclusiveMin, int exclusiveMax)
    {
        if (inclusiveMin >= exclusiveMax) throw new ArgumentOutOfRangeException(nameof(exclusiveMax));
        ulong range = (ulong)((long)exclusiveMax - inclusiveMin);
        return (int)(inclusiveMin + (long)NextBounded(range));
    }

    public double NextDouble() => (NextUInt64() >> 11) * (1d / (1UL << 53));

    public IDeterministicRandom Fork(string salt)
    {
        ArgumentNullException.ThrowIfNull(salt);
        return new EntityDeterministicRandom(EntityStableHash.Text(streamSeed, salt));
    }

    private ulong NextBounded(ulong exclusiveMax)
    {
        ulong threshold = unchecked(0UL - exclusiveMax) % exclusiveMax;
        while (true)
        {
            ulong value = NextUInt64();
            if (value >= threshold) return value % exclusiveMax;
        }
    }
}

internal static class EntityStableHash
{
    private const ulong Offset = 0xCBF29CE484222325UL;
    private const ulong Prime = 0x100000001B3UL;

    public static ulong ForSystem(long worldSeed, ulong tick, ModSystemPhase phase, ResourceId system)
    {
        ulong hash = Add(Offset, unchecked((ulong)worldSeed));
        hash = Add(hash, tick);
        hash = Add(hash, unchecked((ulong)(int)phase));
        return Mix(AddText(hash, system.Value));
    }

    public static ulong Text(ulong seed, string text) => Mix(AddText(Add(Offset, seed), text));

    public static ulong Mix(ulong value)
    {
        value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
        value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
        return value ^ (value >> 31);
    }

    private static ulong Add(ulong hash, ulong value)
    {
        for (int shift = 0; shift < 64; shift += 8)
        {
            hash = (hash ^ (byte)(value >> shift)) * Prime;
        }

        return hash;
    }

    private static ulong AddText(ulong hash, string value)
    {
        int count = Encoding.UTF8.GetByteCount(value);
        hash = Add(hash, (ulong)count);
        Span<byte> bytes = count <= 256 ? stackalloc byte[count] : new byte[count];
        Encoding.UTF8.GetBytes(value, bytes);
        foreach (byte item in bytes) hash = (hash ^ item) * Prime;
        return hash;
    }
}
