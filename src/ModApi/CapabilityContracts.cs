namespace Tesseris.ModApi;

/// <summary>Immutable metadata for one optional host capability.</summary>
public sealed record ModCapabilityDescriptor(ResourceId Id, string Version, Type ContractType);

/// <summary>
/// Discovers optional platform features without growing the legacy <see cref="IModContext"/>.
/// The descriptor snapshot is ordered by ID and does not change during one mod-host lifetime.
/// Capability objects document their own thread and lifetime rules and must not expose mutable engine objects.
/// </summary>
public interface IModCapabilityProvider
{
    IReadOnlyList<ModCapabilityDescriptor> Available { get; }

    bool TryGet<TContract>(ResourceId id, out TContract? capability) where TContract : class;
}

/// <summary>
/// Additive v2 context. Existing mods continue to receive an <see cref="IModContext"/>; a v2-aware
/// mod checks for this interface and can degrade gracefully when an older host is used.
/// All registries returned here are scoped to <see cref="IModContext.Mod"/> and enforce its namespace.
/// </summary>
public interface IModContextV2 : IModContext
{
    IModCapabilityProvider CapabilitiesV2 { get; }

    IModServiceRegistry InterModServices { get; }

    IModEventBus EventBus { get; }

    IModEntityPlatform Entities { get; }

    IModStackPlatform ItemStacks { get; }

    IModContainerPlatform Containers { get; }

    IModWorldDefinitionRegistry WorldDefinitions { get; }

    IModClientPlatform Client { get; }

    IModNetworkRegistry Network { get; }

    IModSerializationRegistry Serialization { get; }
}

/// <summary>Well-known capability IDs. Hosts may expose additional namespaced capabilities.</summary>
public static class ModCapabilityIds
{
    public static readonly ResourceId Services = new("tesseris:services/v2");
    public static readonly ResourceId Events = new("tesseris:events/v2");
    public static readonly ResourceId Entities = new("tesseris:entities/v2");
    public static readonly ResourceId ItemStacks = new("tesseris:item_stacks/v2");
    public static readonly ResourceId Containers = new("tesseris:containers/v2");
    public static readonly ResourceId WorldDefinitions = new("tesseris:world_definitions/v2");
    public static readonly ResourceId Client = new("tesseris:client/v2");
    public static readonly ResourceId Network = new("tesseris:network/v2");
    public static readonly ResourceId Serialization = new("tesseris:serialization/v2");
}
