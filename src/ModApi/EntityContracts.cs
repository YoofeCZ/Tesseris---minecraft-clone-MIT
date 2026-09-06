namespace Tesseris.ModApi;

/// <summary>Runtime entity handle. Generation prevents a destroyed numeric ID from becoming valid again.</summary>
public readonly record struct ModEntityId(ulong Value, uint Generation);

public readonly record struct ModComponentValue(ResourceId ComponentId, ModSerializedValue Value);

/// <summary>Immutable entity snapshot whose components are sorted by component ID.</summary>
public sealed record ModEntitySnapshot(
    ModEntityId Id,
    ResourceId ArchetypeId,
    IReadOnlyList<ModComponentValue> Components,
    uint Revision);

public sealed record ModComponentDescriptor(
    ResourceId Id,
    ResourceId SerializerId,
    bool Replicated = false,
    bool Persisted = true);

public sealed record ModEntityArchetypeDefinition(
    ResourceId Id,
    IReadOnlyList<ModComponentValue> InitialComponents);

public enum ModSystemPhase
{
    PreSimulation = 0,
    Simulation = 100,
    PostSimulation = 200,
    Presentation = 300
}

/// <summary>
/// Read-only entity query valid only during one system callback. Enumeration order is ascending entity ID.
/// Returned snapshots must not be retained after the callback unless copied by the mod.
/// </summary>
public interface IModEntityQuery
{
    IReadOnlyList<ModEntitySnapshot> WithAll(IReadOnlyList<ResourceId> requiredComponents);

    bool TryGet(ModEntityId entity, out ModEntitySnapshot? snapshot);
}

/// <summary>
/// Deferred entity mutations. Commands are committed atomically after the current system phase in system
/// order, then issue order. An invalid expected revision rejects that command without exposing partial state.
/// </summary>
public interface IModEntityCommandBuffer
{
    void Create(ResourceId archetypeId, ResourceId requestId);

    void Destroy(ModEntityId entity, uint expectedRevision);

    void SetComponent(ModEntityId entity, uint expectedRevision, ModComponentValue component);

    void RemoveComponent(ModEntityId entity, uint expectedRevision, ResourceId componentId);
}

public interface IModSystemContext
{
    ulong Tick { get; }

    TimeSpan Delta { get; }

    IModEntityQuery Entities { get; }

    IModEntityCommandBuffer Commands { get; }
}

public interface IModSystem
{
    void Execute(IModSystemContext context);
}

/// <summary>
/// Configuration-time ECS registry. Component, archetype and system IDs are owner-namespaced. Systems run
/// on the game thread in phase, priority, ID order; failure is attributed to the owner and aborts that phase.
/// </summary>
public interface IModEntityPlatform
{
    void RegisterComponent(ModComponentDescriptor descriptor);

    void RegisterArchetype(ModEntityArchetypeDefinition definition);

    void RegisterSystem(ResourceId id, ModSystemPhase phase, int priority, IModSystem system);
}
