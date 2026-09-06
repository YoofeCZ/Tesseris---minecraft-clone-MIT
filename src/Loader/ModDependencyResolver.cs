using System.Security.Cryptography;
using System.Text;
using Tesseris.Loader.Abstractions;

namespace Tesseris.Loader;

public interface IModTrustPolicy
{
    ModTrustDecision Evaluate(ModPackageDescriptor package);
}

/// <summary>
/// Default policy accepting trust levels that were explicitly validated by manifest discovery. Acceptance is
/// execution consent only; managed and core code remain unsandboxed and have the game's process permissions.
/// </summary>
public sealed class ExplicitManifestTrustPolicy : IModTrustPolicy
{
    public static readonly ExplicitManifestTrustPolicy Instance = new();

    private ExplicitManifestTrustPolicy() { }

    public ModTrustDecision Evaluate(ModPackageDescriptor package) => new(
        package.Id,
        package.Trust,
        Allowed: true,
        package.PackageHash,
        package.Trust == ModTrustLevel.ContentOnly
            ? "Content-only package contains no managed entrypoint."
            : "Manifest explicitly opted into trusted in-process code; this is not a sandbox or safety guarantee.");
}

public sealed record LoaderCompatibilityOptions(
    string LoaderVersion,
    string GameVersion,
    IReadOnlyList<string> SupportedModApiVersions,
    IModTrustPolicy TrustPolicy)
{
    public static LoaderCompatibilityOptions Create(
        string gameVersion,
        IModTrustPolicy? trustPolicy = null) => new(
            TesserisLoaderApiInfo.CurrentVersion,
            gameVersion,
            new[] { Tesseris.ModApi.ModApiInfo.CurrentVersion, Tesseris.ModApi.ModApiInfo.LatestContractVersion },
            trustPolicy ?? ExplicitManifestTrustPolicy.Instance);
}

public static class ModDependencyResolver
{
    public static ModLoadPlan Resolve(
        IReadOnlyList<DiscoveredModPackage> discovered,
        LoaderCompatibilityOptions options)
    {
        ArgumentNullException.ThrowIfNull(discovered);
        ArgumentNullException.ThrowIfNull(options);
        _ = SemanticVersion.Parse(options.LoaderVersion);
        _ = SemanticVersion.Parse(options.GameVersion);
        if (options.SupportedModApiVersions.Count == 0)
            throw new ArgumentException("At least one supported ModApi version is required.", nameof(options));
        foreach (string apiVersion in options.SupportedModApiVersions) _ = SemanticVersion.Parse(apiVersion);

        var byId = new Dictionary<string, DiscoveredModPackage>(StringComparer.Ordinal);
        foreach (DiscoveredModPackage candidate in discovered.OrderBy(item => item.ManifestPath, StringComparer.Ordinal))
        {
            ModPackageDescriptor package = candidate.Package;
            if (!byId.TryAdd(package.Id, candidate))
                throw new LoaderException(
                    $"Duplicate mod ID '{package.Id}' in '{candidate.ManifestPath}' and '{byId[package.Id].ManifestPath}'.");

            if (!VersionConstraint.Parse(package.LoaderVersion).Allows(options.LoaderVersion))
                throw new LoaderException(
                    $"Mod '{package.Id}' requires loader '{package.LoaderVersion}', but {options.LoaderVersion} is running ({candidate.ManifestPath}).");
            if (!VersionConstraint.Parse(candidate.GameVersionConstraint).Allows(options.GameVersion))
                throw new LoaderException(
                    $"Mod '{package.Id}' requires game '{candidate.GameVersionConstraint}', but {options.GameVersion} is running ({candidate.ManifestPath}).");
            VersionConstraint api = VersionConstraint.Parse(package.ModApiVersion);
            if (!options.SupportedModApiVersions.Any(api.Allows))
                throw new LoaderException(
                    $"Mod '{package.Id}' requires ModApi '{package.ModApiVersion}', but supported versions are "
                    + $"{string.Join(", ", options.SupportedModApiVersions)} ({candidate.ManifestPath}).");
        }

        var outgoing = byId.Keys.ToDictionary(id => id, _ => new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
        var indegree = byId.Keys.ToDictionary(id => id, _ => 0, StringComparer.Ordinal);
        foreach (DiscoveredModPackage candidate in byId.Values.OrderBy(item => item.Package.Id, StringComparer.Ordinal))
        {
            foreach (ModPackageDependency dependency in candidate.Package.Dependencies)
            {
                bool installed = byId.TryGetValue(dependency.ModId, out DiscoveredModPackage? target);
                bool versionMatches = installed
                    && VersionConstraint.Parse(dependency.VersionConstraint).Allows(target!.Package.Version);
                switch (dependency.Kind)
                {
                    case ModDependencyKind.Required:
                        RequirePresentAndCompatible(candidate, dependency, target, installed, versionMatches);
                        AddEdge(dependency.ModId, candidate.Package.Id, outgoing, indegree);
                        break;
                    case ModDependencyKind.Optional:
                        if (installed && !versionMatches)
                            throw DependencyVersionError(candidate, dependency, target!);
                        if (installed) AddEdge(dependency.ModId, candidate.Package.Id, outgoing, indegree);
                        break;
                    case ModDependencyKind.Incompatible:
                        if (versionMatches)
                            throw new LoaderException(
                                $"Mod '{candidate.Package.Id}' conflicts with installed '{dependency.ModId}' "
                                + $"version {target!.Package.Version} ({candidate.ManifestPath}).");
                        break;
                    case ModDependencyKind.LoadBefore:
                        if (versionMatches) AddEdge(candidate.Package.Id, dependency.ModId, outgoing, indegree);
                        break;
                    case ModDependencyKind.LoadAfter:
                        if (versionMatches) AddEdge(dependency.ModId, candidate.Package.Id, outgoing, indegree);
                        break;
                    default:
                        throw new LoaderException(
                            $"Mod '{candidate.Package.Id}' has unsupported dependency kind {dependency.Kind} ({candidate.ManifestPath}).");
                }
            }
        }

        var ready = new SortedSet<string>(indegree.Where(pair => pair.Value == 0).Select(pair => pair.Key), StringComparer.Ordinal);
        var ordered = new List<DiscoveredModPackage>(byId.Count);
        while (ready.Count > 0)
        {
            string id = ready.Min!;
            ready.Remove(id);
            ordered.Add(byId[id]);
            foreach (string dependent in outgoing[id].Order(StringComparer.Ordinal))
            {
                if (--indegree[dependent] == 0) ready.Add(dependent);
            }
        }

        if (ordered.Count != byId.Count)
        {
            string cycle = string.Join(", ", indegree.Where(pair => pair.Value > 0).Select(pair => pair.Key).Order(StringComparer.Ordinal));
            throw new LoaderException($"Mod dependency/load-order cycle detected: {cycle}.");
        }

        var entries = new List<ModLoadPlanEntry>(ordered.Count);
        for (int index = 0; index < ordered.Count; index++)
        {
            DiscoveredModPackage candidate = ordered[index];
            ModTrustDecision decision = options.TrustPolicy.Evaluate(candidate.Package);
            if (!string.Equals(decision.ModId, candidate.Package.Id, StringComparison.Ordinal))
                throw new LoaderException(
                    $"Trust policy returned owner '{decision.ModId}' while evaluating '{candidate.Package.Id}'.");
            if (!decision.Allowed)
                throw new LoaderException(
                    $"Trust denied for mod '{candidate.Package.Id}' ({candidate.ManifestPath}): {decision.Reason}");
            entries.Add(new ModLoadPlanEntry(candidate.Package, index, decision));
        }

        return new ModLoadPlan(entries.ToArray(), Fingerprint(entries));
    }

    private static void RequirePresentAndCompatible(
        DiscoveredModPackage owner,
        ModPackageDependency dependency,
        DiscoveredModPackage? target,
        bool installed,
        bool versionMatches)
    {
        if (!installed)
            throw new LoaderException(
                $"Mod '{owner.Package.Id}' is missing required dependency '{dependency.ModId}' ({owner.ManifestPath}).");
        if (!versionMatches) throw DependencyVersionError(owner, dependency, target!);
    }

    private static LoaderException DependencyVersionError(
        DiscoveredModPackage owner,
        ModPackageDependency dependency,
        DiscoveredModPackage target) => new(
            $"Mod '{owner.Package.Id}' requires optional/required '{dependency.ModId}' version "
            + $"'{dependency.VersionConstraint}', but {target.Package.Version} is installed ({owner.ManifestPath}).");

    private static void AddEdge(
        string before,
        string after,
        Dictionary<string, HashSet<string>> outgoing,
        Dictionary<string, int> indegree)
    {
        if (outgoing[before].Add(after)) indegree[after]++;
    }

    private static string Fingerprint(IReadOnlyList<ModLoadPlanEntry> entries)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (ModLoadPlanEntry entry in entries)
        {
            string line = $"{entry.LoadIndex}\0{entry.Package.Id}\0{entry.Package.Version}\0{entry.Package.PackageHash}\0{entry.TrustDecision.Requested}\n";
            hash.AppendData(Encoding.UTF8.GetBytes(line));
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
}
