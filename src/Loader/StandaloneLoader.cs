using Tesseris.Loader.Abstractions;

namespace Tesseris.Loader;

/// <summary>Pure loader-core facade. It discovers and resolves packages without loading Game or mutating them.</summary>
public static class StandaloneLoader
{
    public static ModLoadPlan DiscoverAndResolve(string modsRoot, LoaderCompatibilityOptions options) =>
        ModDependencyResolver.Resolve(ModManifestDiscovery.Discover(modsRoot), options);

    public static ModLoadPlan DiscoverAndResolve(
        string modsRoot,
        LoaderCompatibilityOptions compatibility,
        ModPackageDiscoveryOptions discovery) =>
        ModDependencyResolver.Resolve(ModManifestDiscovery.Discover(modsRoot, discovery), compatibility);
}
