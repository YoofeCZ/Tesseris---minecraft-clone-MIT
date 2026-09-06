using Tesseris.Loader;
using Tesseris.Loader.Abstractions;
using Tesseris.ModApi;

namespace Tesseris.Game.Modding;

/// <summary>
/// Loads the already-resolved standalone-loader plan into runtime mod instances. Discovery, dependency
/// resolution and prelaunch/coremod execution deliberately remain launcher responsibilities.
/// </summary>
public sealed class StandaloneRuntimeLoader
{
    /// <summary>Creates the legacy metadata view used by ModHost and custom loader selection.</summary>
    public static ModDescriptor ConvertDescriptor(
        ModPackageDescriptor package,
        ModRuntimeEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (environment is not (ModRuntimeEnvironment.Client or ModRuntimeEnvironment.DedicatedServer))
            throw new ArgumentOutOfRangeException(nameof(environment), environment, "Runtime mods require client or dedicated-server environment.");
        return ConvertDescriptorCore(package, SelectEntrypoints(package, environment));
    }

    public StandaloneRuntimeLoadHandle Load(
        ModLoadPlan plan,
        ModRuntimeEnvironment environment,
        ModAssemblyLoader assemblyLoader,
        IModLogger logger)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(assemblyLoader);
        ArgumentNullException.ThrowIfNull(logger);
        if (environment is not (ModRuntimeEnvironment.Client or ModRuntimeEnvironment.DedicatedServer))
            throw new ArgumentOutOfRangeException(nameof(environment), environment, "Runtime mods require client or dedicated-server environment.");

        var packages = new List<StandaloneRuntimePackage>();
        var owned = new List<OwnedRuntimeEntrypoint>();
        try
        {
            foreach (ModLoadPlanEntry planEntry in plan.Entries.OrderBy(entry => entry.LoadIndex))
            {
                ValidateTrust(planEntry);
                ModPackageDescriptor package = planEntry.Package;
                IReadOnlyList<ModEntrypointDescriptor> selected = SelectEntrypoints(package, environment);
                var instances = new List<(ModEntrypointDescriptor Entrypoint, IMod Instance)>(selected.Count);
                foreach (ModEntrypointDescriptor entrypoint in selected)
                {
                    ModAssemblyLoadHandle? assemblyHandle = null;
                    try
                    {
                        assemblyHandle = assemblyLoader.Load(package, entrypoint);
                        IMod instance = assemblyHandle.CreateInstance<IMod>();
                        instances.Add((entrypoint, instance));
                        owned.Add(new(package.Id, entrypoint.Phase, instance, assemblyHandle));
                        logger.Info($"Loaded mod '{package.Id}' {entrypoint.Phase} entrypoint '{entrypoint.Type}'.");
                    }
                    catch (Exception exception)
                    {
                        assemblyHandle?.Dispose();
                        throw Error(package.Id, entrypoint, exception);
                    }
                }

                IMod? composite = instances.Count switch
                {
                    0 => null,
                    _ => new CompositeMod(package.Id, instances.ToArray()),
                };
                packages.Add(new StandaloneRuntimePackage(
                    ConvertDescriptorCore(package, selected),
                    composite,
                    Array.AsReadOnly(package.ContentSources.ToArray())));
            }

            return new StandaloneRuntimeLoadHandle(Array.AsReadOnly(packages.ToArray()), owned, logger);
        }
        catch (Exception exception)
        {
            WeakReference[] rollbackContexts = owned.Select(item => item.Handle.LoadContextReference).ToArray();
            DisposeReverse(owned, logger);
            if (exception is StandaloneRuntimeLoadException attributed)
                throw attributed.WithRollbackContexts(rollbackContexts);
            throw new StandaloneRuntimeLoadException("Standalone runtime loading failed.", exception, rollbackContexts);
        }
    }

    private static IReadOnlyList<ModEntrypointDescriptor> SelectEntrypoints(
        ModPackageDescriptor package,
        ModRuntimeEnvironment environment)
    {
        IGrouping<(ModEntrypointPhase Phase, string Assembly, string Type), ModEntrypointDescriptor>? duplicate = package.Entrypoints
            .Where(entrypoint => entrypoint.Phase is ModEntrypointPhase.Runtime
                or ModEntrypointPhase.Client
                or ModEntrypointPhase.DedicatedServer)
            .GroupBy(entrypoint => (entrypoint.Phase, entrypoint.Assembly, entrypoint.Type))
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            (ModEntrypointPhase duplicatePhase, string duplicateAssembly, string duplicateType) = duplicate.Key;
            throw new StandaloneRuntimeLoadException(
                $"Mod '{package.Id}' duplicates identical {duplicatePhase} entrypoint "
                + $"'{duplicateType}' in '{duplicateAssembly}'.");
        }
        ModEntrypointPhase side = environment == ModRuntimeEnvironment.Client
            ? ModEntrypointPhase.Client
            : ModEntrypointPhase.DedicatedServer;
        ModEntrypointDescriptor[] selected = package.Entrypoints
            .Where(entrypoint => entrypoint.Phase == ModEntrypointPhase.Runtime || entrypoint.Phase == side)
            .OrderBy(entrypoint => entrypoint.Phase)
            .ThenBy(entrypoint => entrypoint.Assembly, StringComparer.Ordinal)
            .ThenBy(entrypoint => entrypoint.Type, StringComparer.Ordinal)
            .ToArray();
        return selected;
    }

    private static void ValidateTrust(ModLoadPlanEntry entry)
    {
        if (!entry.TrustDecision.Allowed)
            throw new StandaloneRuntimeLoadException(
                $"Mod '{entry.Package.Id}' runtime trust was denied: {entry.TrustDecision.Reason}");
        bool hasManagedRuntime = entry.Package.Entrypoints.Any(item => item.Phase is
            ModEntrypointPhase.Runtime or ModEntrypointPhase.Client or ModEntrypointPhase.DedicatedServer);
        if (hasManagedRuntime && entry.Package.Trust == ModTrustLevel.ContentOnly)
            throw new StandaloneRuntimeLoadException(
                $"Content-only mod '{entry.Package.Id}' cannot declare managed runtime entrypoints.");
    }

    private static ModDescriptor ConvertDescriptorCore(
        ModPackageDescriptor package,
        IReadOnlyList<ModEntrypointDescriptor> selected)
    {
        ModEntrypointDescriptor? first = selected.FirstOrDefault();
        string? contentRoot = package.ContentSources.Count == 0
            ? null
            : Path.GetRelativePath(package.Directory, package.ContentSources[0].RootPath);
        ModDependency[] dependencies = package.Dependencies
            .Where(dependency => dependency.Kind == ModDependencyKind.Required)
            .OrderBy(dependency => dependency.ModId, StringComparer.Ordinal)
            .Select(dependency => new ModDependency(dependency.ModId, dependency.VersionConstraint))
            .ToArray();
        return new ModDescriptor(
            package.Id,
            package.Name,
            package.Version,
            package.ModApiVersion,
            Path.GetFullPath(package.Directory),
            first?.Assembly,
            first?.Type,
            contentRoot,
            package.Trust != ModTrustLevel.ContentOnly,
            Array.AsReadOnly(package.Capabilities.ToArray()),
            Array.AsReadOnly(dependencies));
    }

    private static StandaloneRuntimeLoadException Error(
        string modId,
        ModEntrypointDescriptor entrypoint,
        Exception exception) => new(
        $"Mod '{modId}' failed during {entrypoint.Phase} entrypoint '{entrypoint.Type}' from '{entrypoint.Assembly}'.",
        exception);

    internal static void DisposeReverse(IReadOnlyList<OwnedRuntimeEntrypoint> owned, IModLogger logger)
    {
        for (int index = owned.Count - 1; index >= 0; index--)
        {
            OwnedRuntimeEntrypoint item = owned[index];
            try
            {
                if (item.Instance is IDisposable disposable) disposable.Dispose();
            }
            catch (Exception exception)
            {
                logger.Error($"Mod '{item.ModId}' failed while disposing {item.Phase} entrypoint.", exception);
            }
            finally
            {
                item.Handle.Dispose();
            }
        }
    }

    internal sealed record OwnedRuntimeEntrypoint(
        string ModId,
        ModEntrypointPhase Phase,
        IMod Instance,
        ModAssemblyLoadHandle Handle);

    private sealed class CompositeMod : IMod
    {
        private readonly string owner;
        private readonly IReadOnlyList<(ModEntrypointDescriptor Entrypoint, IMod Instance)> instances;

        public CompositeMod(
            string owner,
            IReadOnlyList<(ModEntrypointDescriptor Entrypoint, IMod Instance)> instances)
        {
            this.owner = owner;
            this.instances = instances;
        }

        public void Configure(IModContext context)
        {
            foreach ((ModEntrypointDescriptor entrypoint, IMod instance) in instances)
            {
                try
                {
                    instance.Configure(context);
                }
                catch (Exception exception)
                {
                    throw new StandaloneRuntimeLoadException(
                        $"Mod '{owner}' failed while configuring {entrypoint.Phase} entrypoint "
                        + $"'{entrypoint.Type}' from '{entrypoint.Assembly}'.",
                        exception);
                }
            }
        }
    }
}

public sealed record StandaloneRuntimePackage(
    ModDescriptor Descriptor,
    IMod? Instance,
    IReadOnlyList<ModContentSource> ContentSources);

/// <summary>Owns every returned mod instance and collectible assembly context until disposed.</summary>
public sealed class StandaloneRuntimeLoadHandle : IDisposable
{
    private IReadOnlyList<StandaloneRuntimePackage> packages;
    private IReadOnlyList<StandaloneRuntimeLoader.OwnedRuntimeEntrypoint>? owned;
    private readonly IModLogger logger;

    internal StandaloneRuntimeLoadHandle(
        IReadOnlyList<StandaloneRuntimePackage> packages,
        IReadOnlyList<StandaloneRuntimeLoader.OwnedRuntimeEntrypoint> owned,
        IModLogger logger)
    {
        this.packages = packages;
        this.owned = owned;
        this.logger = logger;
        LoadContextReferences = Array.AsReadOnly(
            owned.Select(item => item.Handle.LoadContextReference).ToArray());
    }

    public IReadOnlyList<StandaloneRuntimePackage> Packages => packages;

    /// <summary>Diagnostics for proving collectible unload after callers release package/instance references.</summary>
    public IReadOnlyList<WeakReference> LoadContextReferences { get; }

    public void Dispose()
    {
        IReadOnlyList<StandaloneRuntimeLoader.OwnedRuntimeEntrypoint>? release = Interlocked.Exchange(ref owned, null);
        if (release is null) return;
        packages = Array.Empty<StandaloneRuntimePackage>();
        StandaloneRuntimeLoader.DisposeReverse(release, logger);
        GC.SuppressFinalize(this);
    }
}

public sealed class StandaloneRuntimeLoadException : Exception
{
    public StandaloneRuntimeLoadException(string message) : this(message, null, Array.Empty<WeakReference>()) { }

    public StandaloneRuntimeLoadException(string message, Exception innerException)
        : this(message, innerException, Array.Empty<WeakReference>()) { }

    internal StandaloneRuntimeLoadException(
        string message,
        Exception? innerException,
        IReadOnlyList<WeakReference> rollbackContexts)
        : base(message, innerException)
    {
        RolledBackLoadContexts = rollbackContexts;
    }

    public IReadOnlyList<WeakReference> RolledBackLoadContexts { get; }

    internal StandaloneRuntimeLoadException WithRollbackContexts(IReadOnlyList<WeakReference> contexts) =>
        new(Message, InnerException, contexts);
}
