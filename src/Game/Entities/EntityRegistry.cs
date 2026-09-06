using Tesseris.ModApi;

namespace Tesseris.Game.Entities;

public sealed record RegisteredEntityComponent(string ModId, ModComponentDescriptor Descriptor);

public sealed record RegisteredEntityArchetype(string ModId, ModEntityArchetypeDefinition Definition);

public sealed record RegisteredEntitySystem(
    string ModId,
    ResourceId Id,
    ModSystemPhase Phase,
    int Priority,
    IModSystem System);

/// <summary>Configuration-time registry for generic mod entity data and systems.</summary>
public sealed class EntityRegistry
{
    private readonly int registrationThreadId = Environment.CurrentManagedThreadId;
    private readonly Dictionary<ResourceId, RegisteredEntityComponent> components = [];
    private readonly Dictionary<ResourceId, RegisteredEntityArchetype> archetypes = [];
    private readonly Dictionary<ResourceId, RegisteredEntitySystem> systems = [];
    private RegisteredEntityComponent[] frozenComponents = [];
    private RegisteredEntityArchetype[] frozenArchetypes = [];
    private RegisteredEntitySystem[] frozenSystems = [];

    public bool IsFrozen { get; private set; }

    public IReadOnlyList<RegisteredEntityComponent> Components => IsFrozen
        ? frozenComponents
        : components.Values.OrderBy(value => value.Descriptor.Id.Value, StringComparer.Ordinal).ToArray();

    public IReadOnlyList<RegisteredEntityArchetype> Archetypes => IsFrozen
        ? frozenArchetypes
        : archetypes.Values.OrderBy(value => value.Definition.Id.Value, StringComparer.Ordinal).ToArray();

    public IReadOnlyList<RegisteredEntitySystem> Systems => IsFrozen
        ? frozenSystems
        : OrderedSystems();

    internal IModEntityPlatform ForMod(string modId)
    {
        EnsureRegistrationThread();
        ValidateModId(modId);
        return new OwnerView(this, modId);
    }

    public void Freeze()
    {
        EnsureRegistrationThread();
        if (IsFrozen)
        {
            return;
        }

        foreach (RegisteredEntityArchetype archetype in archetypes.Values)
        {
            var seen = new HashSet<ResourceId>();
            foreach (ModComponentValue component in archetype.Definition.InitialComponents)
            {
                if (!seen.Add(component.ComponentId))
                {
                    throw new InvalidOperationException(
                        $"Entity archetype '{archetype.Definition.Id}' contains duplicate component '{component.ComponentId}'.");
                }

                ValidateComponentValue(component, archetype.Definition.Id);
            }
        }

        frozenComponents = components.Values
            .OrderBy(value => value.Descriptor.Id.Value, StringComparer.Ordinal)
            .ToArray();
        frozenArchetypes = archetypes.Values
            .OrderBy(value => value.Definition.Id.Value, StringComparer.Ordinal)
            .ToArray();
        frozenSystems = OrderedSystems();
        IsFrozen = true;
    }

    public bool TryGetComponent(ResourceId id, out RegisteredEntityComponent? component) =>
        components.TryGetValue(id, out component);

    public bool TryGetArchetype(ResourceId id, out RegisteredEntityArchetype? archetype) =>
        archetypes.TryGetValue(id, out archetype);

    internal void ValidateComponentValue(ModComponentValue component, ResourceId ownerId)
    {
        EnsureValid(component.ComponentId, nameof(component.ComponentId));
        EnsureValid(component.Value.SerializerId, nameof(component.Value.SerializerId));
        if (!components.TryGetValue(component.ComponentId, out RegisteredEntityComponent? registered))
        {
            throw new InvalidOperationException(
                $"Entity '{ownerId}' references unknown component '{component.ComponentId}'.");
        }

        if (component.Value.SerializerId != registered.Descriptor.SerializerId)
        {
            throw new InvalidOperationException(
                $"Component '{component.ComponentId}' on '{ownerId}' uses serializer "
                + $"'{component.Value.SerializerId}', expected '{registered.Descriptor.SerializerId}'.");
        }

        if (component.Value.SchemaVersion < 0)
        {
            throw new InvalidOperationException(
                $"Component '{component.ComponentId}' on '{ownerId}' has a negative schema version.");
        }
    }

    internal static ModComponentValue Clone(ModComponentValue component) => new(
        component.ComponentId,
        new ModSerializedValue(
            component.Value.SerializerId,
            component.Value.SchemaVersion,
            component.Value.Payload.ToArray()));

    private void RegisterComponent(string modId, ModComponentDescriptor descriptor)
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(descriptor);
        EnsureOwned(modId, descriptor.Id, "component");
        EnsureValid(descriptor.SerializerId, nameof(descriptor.SerializerId));
        if (!components.TryAdd(descriptor.Id, new RegisteredEntityComponent(modId, descriptor)))
        {
            throw new InvalidOperationException($"Entity component '{descriptor.Id}' is already registered.");
        }
    }

    private void RegisterArchetype(string modId, ModEntityArchetypeDefinition definition)
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(definition.InitialComponents);
        EnsureOwned(modId, definition.Id, "archetype");

        ModComponentValue[] initial = definition.InitialComponents
            .Select(Clone)
            .OrderBy(value => value.ComponentId.Value, StringComparer.Ordinal)
            .ToArray();
        var ownedDefinition = new ModEntityArchetypeDefinition(
            definition.Id,
            Array.AsReadOnly(initial));
        if (!archetypes.TryAdd(definition.Id, new RegisteredEntityArchetype(modId, ownedDefinition)))
        {
            throw new InvalidOperationException($"Entity archetype '{definition.Id}' is already registered.");
        }
    }

    private void RegisterSystem(
        string modId,
        ResourceId id,
        ModSystemPhase phase,
        int priority,
        IModSystem system)
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(system);
        EnsureOwned(modId, id, "system");
        if (!Enum.IsDefined(phase))
        {
            throw new ArgumentOutOfRangeException(nameof(phase));
        }

        if (!systems.TryAdd(id, new RegisteredEntitySystem(modId, id, phase, priority, system)))
        {
            throw new InvalidOperationException($"Entity system '{id}' is already registered.");
        }
    }

    private RegisteredEntitySystem[] OrderedSystems() => systems.Values
        .OrderBy(value => value.Phase)
        .ThenBy(value => value.Priority)
        .ThenBy(value => value.Id.Value, StringComparer.Ordinal)
        .ThenBy(value => value.ModId, StringComparer.Ordinal)
        .ToArray();

    private void EnsureMutable()
    {
        EnsureRegistrationThread();
        if (IsFrozen)
        {
            throw new InvalidOperationException("Entity registration is frozen.");
        }
    }

    private void EnsureRegistrationThread()
    {
        if (Environment.CurrentManagedThreadId != registrationThreadId)
        {
            throw new InvalidOperationException("Entity registration must run on the registration thread.");
        }
    }

    private static void EnsureOwned(string modId, ResourceId id, string kind)
    {
        EnsureValid(id, nameof(id));
        int separator = id.Value.IndexOf(':');
        if (!id.Value.AsSpan(0, separator).Equals(modId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Mod '{modId}' may only register entity {kind} IDs in its own namespace.");
        }
    }

    private static void ValidateModId(string modId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modId);
        _ = new ResourceId(modId + ":validation");
    }

    private static void EnsureValid(ResourceId id, string parameter)
    {
        if (string.IsNullOrWhiteSpace(id.Value))
        {
            throw new ArgumentException("A non-default resource ID is required.", parameter);
        }
    }

    private sealed class OwnerView : IModEntityPlatform
    {
        private readonly EntityRegistry registry;
        private readonly string modId;

        public OwnerView(EntityRegistry registry, string modId)
        {
            this.registry = registry;
            this.modId = modId;
        }

        public void RegisterComponent(ModComponentDescriptor descriptor) =>
            registry.RegisterComponent(modId, descriptor);

        public void RegisterArchetype(ModEntityArchetypeDefinition definition) =>
            registry.RegisterArchetype(modId, definition);

        public void RegisterSystem(ResourceId id, ModSystemPhase phase, int priority, IModSystem system) =>
            registry.RegisterSystem(modId, id, phase, priority, system);
    }
}
