using System.Reflection;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Tesseris.Loader;
using Tesseris.Loader.Abstractions;
using Tesseris.Loader.Transforms;
using Tesseris.ModApi;

namespace Tesseris.Launcher;

public sealed class LauncherBootstrap
{
    public int Run(LauncherOptions options, string[] gameArgs)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(gameArgs);
        string installation = Path.GetFullPath(options.InstallationDirectory);
        string gamePath = Path.GetFullPath(options.GameAssemblyPath, installation);
        string modsPath = Path.GetFullPath(options.ModsDirectory, installation);
        string cachePath = Path.GetFullPath(options.CacheDirectory, installation);
        if (!File.Exists(gamePath))
            throw new LauncherBootstrapException($"Game assembly does not exist: {gamePath}");
        EnsureGameNotLoaded(gamePath);

        var compatibility = LoaderCompatibilityOptions.Create(options.GameVersion, options.TrustPolicy);
        ModLoadPlan plan;
        try
        {
            IReadOnlySet<string> disabled = ModEnablementSettings.ReadDisabled(modsPath);
            IReadOnlyList<DiscoveredModPackage> discovered = ModManifestDiscovery.Discover(modsPath)
                .Where(package => !disabled.Contains(package.Package.Id))
                .ToArray();
            plan = ModDependencyResolver.Resolve(discovered, compatibility);
        }
        catch (Exception exception)
        {
            throw new LauncherBootstrapException($"Mod discovery/resolution failed in '{modsPath}'.", exception);
        }
        options.Observer?.OnPhase(new(LauncherPhase.ModsResolved, Detail: plan.Fingerprint));

        var environment = new ModLoaderEnvironment(
            compatibility.LoaderVersion,
            ModApiInfo.LatestContractVersion,
            options.GameVersion,
            RuntimeInformation.FrameworkDescription,
            RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture.ToString(),
            ModRuntimeEnvironment.Client,
            installation,
            gamePath,
            cachePath,
            options.IsDevelopment);
        var services = new ModServiceRegistry();
        var patches = new FrozenPatchRegistry();
        var loader = new ModAssemblyLoader();
        var loaded = new List<LoadedPreLaunchEntrypoint>();
        var patchPaths = new Dictionary<string, string>(StringComparer.Ordinal);
        var patchAssemblies = new Dictionary<string, Assembly>(StringComparer.Ordinal);

        try
        {
            foreach (ModLoadPlanEntry entry in plan.Entries.OrderBy(item => item.LoadIndex))
            {
                EnsureTrust(entry);
                foreach (ModEntrypointDescriptor descriptor in entry.Package.Entrypoints
                             .Where(item => item.Phase is ModEntrypointPhase.PreLaunch or ModEntrypointPhase.CoreMod)
                             .OrderBy(item => item.Phase)
                             .ThenBy(item => item.Assembly, StringComparer.Ordinal)
                             .ThenBy(item => item.Type, StringComparer.Ordinal))
                {
                    ExecuteEntrypoint(entry, descriptor, environment, plan, services, patches, loader,
                        loaded, patchPaths, patchAssemblies, options.Observer);
                }
            }

            IReadOnlyList<ModMethodPatchDescriptor> frozenPatches = patches.Freeze();
            options.Observer?.OnPhase(new(LauncherPhase.PatchRegistryFrozen, Detail: frozenPatches.Count.ToString()));
            EnsureGameNotLoaded(gamePath);
            options.Observer?.OnPhase(new(LauncherPhase.BeforeTransform, AssemblyPath: gamePath));
            string gameToLoad;
            if (frozenPatches.Count == 0)
            {
                gameToLoad = PrepareUnpatchedCopy(gamePath, cachePath);
                options.Observer?.OnPhase(new(
                    LauncherPhase.AfterTransform,
                    Detail: "no-patches",
                    AssemblyPath: gameToLoad));
            }
            else
            {
                CoreModTransformationResult transformed = new CoreModTransformer().Transform(new(
                    gamePath,
                    cachePath,
                    compatibility.LoaderVersion,
                    ModApiInfo.LatestContractVersion,
                    plan,
                    frozenPatches,
                    patchPaths));
                gameToLoad = transformed.OutputAssemblyPath;
                options.Observer?.OnPhase(new(
                    LauncherPhase.AfterTransform,
                    Detail: transformed.CacheHit ? "cache-hit" : "cache-miss",
                    AssemblyPath: transformed.OutputAssemblyPath));
            }
            EnsureGameNotLoaded(gamePath);
            return GameAssemblyInvoker.Invoke(
                gameToLoad,
                gamePath,
                options.GameEntryType,
                options.GameEntryMethod,
                gameArgs,
                patchAssemblies,
                options.Observer);
        }
        catch (LauncherBootstrapException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new LauncherBootstrapException("Launcher bootstrap failed.", exception);
        }
        finally
        {
            var cleanupFailures = new List<Exception>();
            for (int index = loaded.Count - 1; index >= 0; index--)
            {
                LoadedPreLaunchEntrypoint item = loaded[index];
                TryCleanup(
                    () => options.Observer?.OnPhase(new(LauncherPhase.Cleanup, item.ModId, item.Phase.ToString())),
                    item.ModId,
                    "cleanup observer",
                    cleanupFailures);
                if (item.Instance is IDisposable disposable)
                    TryCleanup(disposable.Dispose, item.ModId, "entrypoint disposal", cleanupFailures);
                TryCleanup(() => services.UnregisterOwner(item.ModId), item.ModId, "service unregister", cleanupFailures);
                TryCleanup(item.Handle.Dispose, item.ModId, "assembly unload", cleanupFailures);
            }
            foreach (Exception failure in cleanupFailures)
                Console.Error.WriteLine($"Tesseris launcher cleanup failure: {failure}");
        }
    }

    private static string PrepareUnpatchedCopy(string gamePath, string cachePath)
    {
        using FileStream input = File.OpenRead(gamePath);
        string digest = Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
        string directory = Path.Combine(cachePath, "unpatched", digest);
        string output = Path.Combine(directory, Path.GetFileName(gamePath));
        if (File.Exists(output)) return output;

        Directory.CreateDirectory(directory);
        string temporary = output + $".{Guid.NewGuid():N}.tmp";
        File.Copy(gamePath, temporary, overwrite: false);
        try
        {
            File.Move(temporary, output);
        }
        catch (IOException) when (File.Exists(output))
        {
            File.Delete(temporary);
        }
        return output;
    }

    private static void ExecuteEntrypoint(
        ModLoadPlanEntry planEntry,
        ModEntrypointDescriptor descriptor,
        ModLoaderEnvironment environment,
        ModLoadPlan plan,
        ModServiceRegistry services,
        FrozenPatchRegistry patches,
        ModAssemblyLoader loader,
        List<LoadedPreLaunchEntrypoint> loaded,
        Dictionary<string, string> patchPaths,
        Dictionary<string, Assembly> patchAssemblies,
        ILauncherObserver? observer)
    {
        string modId = planEntry.Package.Id;
        LauncherPhase phase = descriptor.Phase == ModEntrypointPhase.CoreMod
            ? LauncherPhase.CoreMod
            : LauncherPhase.PreLaunch;
        observer?.OnPhase(new(phase, modId, descriptor.Type));
        ModAssemblyLoadHandle? handle = null;
        object? instance = null;
        try
        {
            handle = loader.Load(planEntry.Package, descriptor);
            var common = new PreLaunchContext(
                planEntry.Package,
                environment,
                plan,
                EmptyLoaderCapabilities.Instance,
                services.ForOwner(modId),
                new ConsoleModLogger(modId));
            if (descriptor.Phase == ModEntrypointPhase.PreLaunch)
            {
                instance = handle.CreateInstance<IPreLaunchMod>();
                ((IPreLaunchMod)instance).PreLaunch(common);
            }
            else
            {
                instance = handle.CreateInstance<ICoreMod>();
                string assemblyName = handle.Assembly.GetName().Name
                    ?? throw new LauncherBootstrapException($"Coremod '{modId}' entry assembly has no identity.");
                string assemblyPath = Path.GetFullPath(Path.Combine(planEntry.Package.Directory, descriptor.Assembly));
                if (!patchPaths.TryAdd(modId, assemblyPath))
                    throw new LauncherBootstrapException(
                        $"Coremod '{modId}' declares more than one core entry assembly; one owner must have one patch assembly.");
                if (!patchAssemblies.TryAdd(assemblyName, handle.Assembly))
                    throw new LauncherBootstrapException(
                        $"Coremod '{modId}' patch assembly identity '{assemblyName}' conflicts with another coremod.");
                var context = new CoreModContext(
                    common.Mod,
                    common.Environment,
                    common.LoadPlan,
                    common.Capabilities,
                    common.Services,
                    common.Logger,
                    patches.ForOwner(modId, assemblyName));
                ((ICoreMod)instance).ConfigureCore(context);
            }
            loaded.Add(new(modId, descriptor.Phase, instance, handle));
        }
        catch (Exception exception)
        {
            if (instance is IDisposable disposable) disposable.Dispose();
            handle?.Dispose();
            services.UnregisterOwner(modId);
            throw new LauncherBootstrapException(
                $"Mod '{modId}' failed during {descriptor.Phase} entrypoint '{descriptor.Type}'.", exception);
        }
    }

    private static void EnsureTrust(ModLoadPlanEntry entry)
    {
        if (!entry.TrustDecision.Allowed)
            throw new LauncherBootstrapException(
                $"Mod '{entry.Package.Id}' trust policy denied {entry.Package.Trust}: {entry.TrustDecision.Reason}");
        bool hasCore = entry.Package.Entrypoints.Any(item => item.Phase == ModEntrypointPhase.CoreMod);
        bool hasPreLaunch = entry.Package.Entrypoints.Any(item => item.Phase == ModEntrypointPhase.PreLaunch);
        if (hasCore && entry.Package.Trust != ModTrustLevel.CoreMod)
            throw new LauncherBootstrapException($"Mod '{entry.Package.Id}' core entrypoint lacks CoreMod trust.");
        if (hasPreLaunch && entry.Package.Trust < ModTrustLevel.PreLaunch)
            throw new LauncherBootstrapException($"Mod '{entry.Package.Id}' prelaunch entrypoint lacks PreLaunch trust.");
    }

    [UnconditionalSuppressMessage(
        "SingleFile",
        "IL3002",
        Justification = "Bundled launcher modules report <Unknown> and are skipped; the game is always a physical cache file.")]
    private static void EnsureGameNotLoaded(string gamePath)
    {
        string expected = Path.GetFullPath(gamePath);
        Assembly? loaded = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(assembly =>
        {
            try
            {
                string location = assembly.ManifestModule.FullyQualifiedName;
                return location.Length > 0 && location[0] != '<'
                    && string.Equals(Path.GetFullPath(location), expected, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception exception) when (exception is NotSupportedException or ArgumentException) { return false; }
        });
        if (loaded is not null)
            throw new LauncherBootstrapException(
                $"Game assembly '{loaded.FullName}' was loaded before coremod transformation ({expected}).");
    }

    private static void TryCleanup(
        Action action,
        string modId,
        string operation,
        ICollection<Exception> failures)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            failures.Add(new LauncherBootstrapException(
                $"Mod '{modId}' failed during reverse-order {operation}.", exception));
        }
    }

    private sealed record LoadedPreLaunchEntrypoint(
        string ModId,
        ModEntrypointPhase Phase,
        object Instance,
        ModAssemblyLoadHandle Handle);
}
