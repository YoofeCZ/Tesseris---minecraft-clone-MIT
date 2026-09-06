namespace Tesseris.ModApi;

/// <summary>Stable semantic version and owner information for a published inter-mod service.</summary>
public sealed record ModServiceDescriptor(
    ResourceId Id,
    string Version,
    string OwnerModId,
    Type ContractType);

/// <summary>
/// Versioned service exchange between mods. Publishing and removal are game-thread-only. Lookups return
/// immutable descriptor snapshots in ordinal ID order. A host removes every service before unloading its
/// owner. Contract types crossing load contexts must come from ModApi or a loader-approved shared contract
/// assembly; dependency isolation is not a security sandbox.
/// </summary>
public interface IModServiceRegistry
{
    IReadOnlyList<ModServiceDescriptor> Published { get; }

    void Publish<TContract>(ResourceId id, string version, TContract service) where TContract : class;

    bool TryGet<TContract>(ResourceId id, string versionConstraint, out TContract? service)
        where TContract : class;

    bool Remove(ResourceId id);
}
