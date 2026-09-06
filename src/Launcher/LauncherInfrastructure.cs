using Tesseris.Loader;
using Tesseris.Loader.Abstractions;
using Tesseris.ModApi;

namespace Tesseris.Launcher;

internal sealed class FrozenPatchRegistry
{
    private readonly List<ModMethodPatchDescriptor> patches = new();
    private bool frozen;

    public IReadOnlyList<ModMethodPatchDescriptor> Freeze()
    {
        frozen = true;
        return patches.ToArray();
    }

    public IModPatchRegistry ForOwner(string owner, string patchAssemblyName) =>
        new OwnerView(this, owner, patchAssemblyName);

    private void Register(string owner, string patchAssemblyName, ModMethodPatchDescriptor descriptor)
    {
        if (frozen) throw new LauncherBootstrapException("Coremod patch registry is frozen.");
        ArgumentNullException.ThrowIfNull(descriptor);
        if (!string.Equals(owner, descriptor.OwnerModId, StringComparison.Ordinal))
            throw new LauncherBootstrapException(
                $"Coremod '{owner}' cannot register patch '{descriptor.Id}' for owner '{descriptor.OwnerModId}'.");
        if (!descriptor.Id.Value.StartsWith(owner + ":", StringComparison.Ordinal))
            throw new LauncherBootstrapException(
                $"Coremod '{owner}' patch '{descriptor.Id}' is outside its namespace.");
        if (!string.Equals(descriptor.Entrypoint.AssemblyName, patchAssemblyName, StringComparison.Ordinal))
            throw new LauncherBootstrapException(
                $"Coremod '{owner}' patch '{descriptor.Id}' names entry assembly "
                + $"'{descriptor.Entrypoint.AssemblyName}', expected loaded core assembly '{patchAssemblyName}'.");
        if (patches.Any(existing => existing.Id == descriptor.Id))
            throw new LauncherBootstrapException($"Coremod '{owner}' duplicated patch ID '{descriptor.Id}'.");
        patches.Add(descriptor);
    }

    private sealed class OwnerView : IModPatchRegistry
    {
        private readonly FrozenPatchRegistry registry;
        private readonly string owner;
        private readonly string patchAssemblyName;

        public OwnerView(FrozenPatchRegistry registry, string owner, string patchAssemblyName)
        {
            this.registry = registry;
            this.owner = owner;
            this.patchAssemblyName = patchAssemblyName;
        }

        public IReadOnlyList<ModMethodPatchDescriptor> Registered => registry.patches
            .Where(patch => string.Equals(patch.OwnerModId, owner, StringComparison.Ordinal))
            .ToArray();

        public void Register(ModMethodPatchDescriptor descriptor) =>
            registry.Register(owner, patchAssemblyName, descriptor);
    }
}

internal sealed class EmptyLoaderCapabilities : ILoaderCapabilityProvider
{
    public static readonly EmptyLoaderCapabilities Instance = new();

    public IReadOnlyList<ModCapabilityDescriptor> Available => Array.Empty<ModCapabilityDescriptor>();

    public bool TryGet<TContract>(ResourceId id, out TContract? capability) where TContract : class
    {
        capability = null;
        return false;
    }
}

internal sealed class ConsoleModLogger : IModLogger
{
    private readonly string owner;

    public ConsoleModLogger(string owner) => this.owner = owner;

    public void Info(string message) => Console.WriteLine($"[mod:{owner}] {message}");

    public void Warning(string message) => Console.Error.WriteLine($"[mod:{owner}:warning] {message}");

    public void Error(string message, Exception? exception = null) =>
        Console.Error.WriteLine($"[mod:{owner}:error] {message}{(exception is null ? string.Empty : $" {exception}")}");
}

internal sealed record PreLaunchContext(
    ModPackageDescriptor Mod,
    ModLoaderEnvironment Environment,
    ModLoadPlan LoadPlan,
    ILoaderCapabilityProvider Capabilities,
    IModServiceRegistry Services,
    IModLogger Logger) : IPreLaunchContext;

internal sealed record CoreModContext(
    ModPackageDescriptor Mod,
    ModLoaderEnvironment Environment,
    ModLoadPlan LoadPlan,
    ILoaderCapabilityProvider Capabilities,
    IModServiceRegistry Services,
    IModLogger Logger,
    IModPatchRegistry Patches) : ICoreModContext;
