namespace Tesseris.ModApi;

/// <summary>Immutable metadata supplied to mods and loaders after manifest validation.</summary>
public sealed record ModDescriptor(
    string Id,
    string Name,
    string Version,
    string ApiVersion,
    string Directory,
    string? EntryAssembly,
    string? EntryType,
    string? ContentRoot,
    bool TrustedCode,
    IReadOnlyList<string> Capabilities,
    IReadOnlyList<ModDependency> Dependencies);

public sealed record ModDependency(string Id, string Version);

public sealed record ModContentSource(string ModId, string RootPath);
