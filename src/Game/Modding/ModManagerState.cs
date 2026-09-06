using Tesseris.Loader;
using Tesseris.Loader.Abstractions;

namespace Tesseris.Game.Modding;

public sealed record InstalledModInfo(
    string Id,
    string Name,
    string Version,
    ModTrustLevel Trust,
    string ManifestPath,
    bool Enabled,
    bool Loaded);

/// <summary>
/// Persistent pre-launch mod selection. Required dependency changes are propagated so the loader never
/// receives a deliberately incomplete package graph.
/// </summary>
public sealed class ModManagerState
{
    private readonly string modsRoot;
    private readonly IReadOnlyList<DiscoveredModPackage> packages;
    private readonly Dictionary<string, DiscoveredModPackage> byId;
    private readonly HashSet<string> loadedEnabled;
    private readonly HashSet<string> desiredEnabled;

    private ModManagerState(
        string modsRoot,
        IReadOnlyList<DiscoveredModPackage> packages,
        HashSet<string> enabled)
    {
        this.modsRoot = modsRoot;
        this.packages = packages;
        byId = packages.ToDictionary(package => package.Package.Id, StringComparer.Ordinal);
        loadedEnabled = new HashSet<string>(enabled, StringComparer.Ordinal);
        desiredEnabled = new HashSet<string>(enabled, StringComparer.Ordinal);
        NormalizeRequiredDependencies(desiredEnabled);
    }

    public IReadOnlySet<string> EnabledModIds => loadedEnabled;

    public bool HasPendingChanges => !loadedEnabled.SetEquals(desiredEnabled);

    public IReadOnlyList<InstalledModInfo> Mods => packages
        .Select(package => new InstalledModInfo(
            package.Package.Id,
            package.Package.Name,
            package.Package.Version,
            package.Package.Trust,
            package.ManifestPath,
            desiredEnabled.Contains(package.Package.Id),
            loadedEnabled.Contains(package.Package.Id)))
        .ToArray();

    public static ModManagerState Load(string modsRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modsRoot);
        string root = Path.GetFullPath(modsRoot);
        Directory.CreateDirectory(root);
        IReadOnlyList<DiscoveredModPackage> packages = ModManifestDiscovery.Discover(root)
            .OrderBy(package => package.Package.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(package => package.Package.Id, StringComparer.Ordinal)
            .ToArray();

        HashSet<string> disabled = ModEnablementSettings.ReadDisabled(root).ToHashSet(StringComparer.Ordinal);
        var enabled = packages
            .Select(package => package.Package.Id)
            .Where(id => !disabled.Contains(id))
            .ToHashSet(StringComparer.Ordinal);

        var result = new ModManagerState(root, packages, enabled);
        result.NormalizeRequiredDependencies(result.loadedEnabled);
        result.desiredEnabled.Clear();
        result.desiredEnabled.UnionWith(result.loadedEnabled);
        return result;
    }

    public bool Toggle(string modId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modId);
        if (!byId.ContainsKey(modId)) return false;

        if (desiredEnabled.Contains(modId))
        {
            DisableWithDependents(modId);
        }
        else
        {
            EnableWithDependencies(modId, new HashSet<string>(StringComparer.Ordinal));
        }
        return true;
    }

    public void Save()
    {
        string[] disabled = packages
            .Select(package => package.Package.Id)
            .Where(id => !desiredEnabled.Contains(id))
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        ModEnablementSettings.WriteDisabled(modsRoot, disabled);
    }

    private void DisableWithDependents(string modId)
    {
        if (!desiredEnabled.Remove(modId)) return;
        string[] dependents = packages
            .Where(package => desiredEnabled.Contains(package.Package.Id))
            .Where(package => RequiredDependencies(package).Contains(modId, StringComparer.Ordinal))
            .Select(package => package.Package.Id)
            .ToArray();
        foreach (string dependent in dependents) DisableWithDependents(dependent);
    }

    private void EnableWithDependencies(string modId, HashSet<string> visiting)
    {
        if (!visiting.Add(modId)) return;
        if (!byId.TryGetValue(modId, out DiscoveredModPackage? package)) return;
        foreach (string dependency in RequiredDependencies(package))
        {
            EnableWithDependencies(dependency, visiting);
        }
        desiredEnabled.Add(modId);
        visiting.Remove(modId);
    }

    private void NormalizeRequiredDependencies(HashSet<string> enabled)
    {
        bool changed;
        do
        {
            changed = false;
            foreach (DiscoveredModPackage package in packages)
            {
                if (!enabled.Contains(package.Package.Id)) continue;
                if (RequiredDependencies(package).Any(dependency => !enabled.Contains(dependency)))
                {
                    enabled.Remove(package.Package.Id);
                    changed = true;
                }
            }
        }
        while (changed);
    }

    private static IEnumerable<string> RequiredDependencies(DiscoveredModPackage package) =>
        package.Package.Dependencies
            .Where(dependency => dependency.Kind == ModDependencyKind.Required)
            .Select(dependency => dependency.ModId);

}
