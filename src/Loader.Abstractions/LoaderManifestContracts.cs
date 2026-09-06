using Tesseris.ModApi;

namespace Tesseris.Loader.Abstractions;

public enum ModEntrypointPhase
{
    PreLaunch,
    CoreMod,
    Runtime,
    Client,
    DedicatedServer
}

public sealed record ModEntrypointDescriptor(
    ModEntrypointPhase Phase,
    string Assembly,
    string Type);

public enum ModDependencyKind
{
    Required,
    Optional,
    Incompatible,
    LoadBefore,
    LoadAfter
}

public sealed record ModPackageDependency(
    string ModId,
    string VersionConstraint,
    ModDependencyKind Kind);

/// <summary>
/// Validated v2 package metadata. All list properties are immutable snapshots sorted by their documented
/// stable key. Legacy manifests are adapted into this model without changing their original runtime entrypoint.
/// </summary>
public sealed record ModPackageDescriptor(
    int SchemaVersion,
    string Id,
    string Name,
    string Version,
    string LoaderVersion,
    string ModApiVersion,
    string Directory,
    ModTrustLevel Trust,
    IReadOnlyList<ModEntrypointDescriptor> Entrypoints,
    IReadOnlyList<ModPackageDependency> Dependencies,
    IReadOnlyList<string> SharedAssemblies,
    IReadOnlyList<ModContentSource> ContentSources,
    IReadOnlyList<string> Capabilities,
    string PackageHash);

public sealed record ModLoadPlanEntry(
    ModPackageDescriptor Package,
    int LoadIndex,
    ModTrustDecision TrustDecision);

/// <summary>Immutable, topologically sorted load plan. Entries are never reordered after prelaunch begins.</summary>
public sealed record ModLoadPlan(
    IReadOnlyList<ModLoadPlanEntry> Entries,
    string Fingerprint);
