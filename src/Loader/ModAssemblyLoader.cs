using System.Reflection;
using System.Runtime.Loader;
using Tesseris.Loader.Abstractions;
using Tesseris.ModApi;

namespace Tesseris.Loader;

public interface ISharedAssemblyCatalog
{
    bool TryResolve(AssemblyName requested, out Assembly? assembly);
}

public sealed class EmptySharedAssemblyCatalog : ISharedAssemblyCatalog
{
    public static readonly EmptySharedAssemblyCatalog Instance = new();

    private EmptySharedAssemblyCatalog() { }

    public bool TryResolve(AssemblyName requested, out Assembly? assembly)
    {
        assembly = null;
        return false;
    }
}

/// <summary>Loads one managed mod entrypoint into a private collectible dependency context.</summary>
public sealed class ModAssemblyLoader
{
    private static readonly IReadOnlyDictionary<string, Assembly> PlatformAssemblies =
        new Dictionary<string, Assembly>(StringComparer.Ordinal)
        {
            [typeof(IMod).Assembly.GetName().Name!] = typeof(IMod).Assembly,
            [typeof(IPreLaunchMod).Assembly.GetName().Name!] = typeof(IPreLaunchMod).Assembly,
        };

    private readonly ISharedAssemblyCatalog sharedAssemblies;

    public ModAssemblyLoader(ISharedAssemblyCatalog? sharedAssemblies = null) =>
        this.sharedAssemblies = sharedAssemblies ?? EmptySharedAssemblyCatalog.Instance;

    public ModAssemblyLoadHandle Load(ModPackageDescriptor package, ModEntrypointDescriptor entrypoint)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(entrypoint);
        if (package.Trust == ModTrustLevel.ContentOnly)
            throw new LoaderException($"Content-only mod '{package.Id}' cannot load managed entrypoint '{entrypoint.Type}'.");
        if (!package.Entrypoints.Contains(entrypoint))
            throw new LoaderException($"Entrypoint '{entrypoint.Type}' is not declared by mod '{package.Id}'.");

        string packageRoot = Path.GetFullPath(package.Directory);
        DetectBundledPlatformAssemblies(package.Id, packageRoot);
        string assemblyPath = ResolveSafePackagePath(package.Id, packageRoot, entrypoint.Assembly);
        if (!File.Exists(assemblyPath))
            throw new LoaderException($"Mod '{package.Id}' entry assembly does not exist: {assemblyPath}");

        var context = new IsolatedModLoadContext(package, assemblyPath, sharedAssemblies, PlatformAssemblies);
        try
        {
            Assembly assembly;
            using (FileStream assemblyStream = new(assemblyPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                string symbolsPath = Path.ChangeExtension(assemblyPath, ".pdb");
                if (File.Exists(symbolsPath))
                {
                    using FileStream symbolsStream = new(symbolsPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    assembly = context.LoadFromStream(assemblyStream, symbolsStream);
                }
                else
                {
                    assembly = context.LoadFromStream(assemblyStream);
                }
            }

            Type? type = assembly.GetType(entrypoint.Type, throwOnError: false, ignoreCase: false);
            if (type is null)
                throw new LoaderException(
                    $"Mod '{package.Id}' entry type '{entrypoint.Type}' was not found in '{assemblyPath}'.");
            return new ModAssemblyLoadHandle(package.Id, entrypoint, assembly, type, context);
        }
        catch
        {
            context.Unload();
            throw;
        }
    }

    private static void DetectBundledPlatformAssemblies(string modId, string packageRoot)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = false,
            ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };
        foreach (string file in Directory.EnumerateFiles(packageRoot, "*.dll", options).Order(StringComparer.Ordinal))
        {
            RejectReparseSegments(modId, packageRoot, file);
            AssemblyName identity;
            try
            {
                identity = AssemblyName.GetAssemblyName(file);
            }
            catch (BadImageFormatException)
            {
                continue;
            }
            if (identity.Name is not null && PlatformAssemblies.ContainsKey(identity.Name))
            {
                throw new LoaderException(
                    $"Mod '{modId}' bundles platform assembly '{identity.Name}' at '{file}'. "
                    + "Tesseris.ModApi and Tesseris.Loader.Abstractions must always be supplied by the host.");
            }
        }
    }

    private static string ResolveSafePackagePath(string modId, string packageRoot, string relativePath)
    {
        string input = Path.IsPathRooted(relativePath)
            ? Path.GetRelativePath(packageRoot, relativePath)
            : relativePath;
        string result = ModManifestDiscovery.ResolveInside(packageRoot, input, packageRoot);
        RejectReparseSegments(modId, packageRoot, result);
        return result;
    }

    private static void RejectReparseSegments(string modId, string packageRoot, string path)
    {
        string current = packageRoot;
        string relative = Path.GetRelativePath(packageRoot, path);
        foreach (string segment in relative.Split(
                     new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if ((File.Exists(current) || Directory.Exists(current))
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new LoaderException(
                    $"Mod '{modId}' assembly path traverses a symbolic link/reparse point: {current}");
            }
        }
    }

    private sealed class IsolatedModLoadContext : AssemblyLoadContext
    {
        private readonly ModPackageDescriptor package;
        private readonly AssemblyDependencyResolver resolver;
        private readonly ISharedAssemblyCatalog sharedAssemblies;
        private readonly IReadOnlyDictionary<string, Assembly> platformAssemblies;
        private readonly HashSet<string> declaredShared;

        public IsolatedModLoadContext(
            ModPackageDescriptor package,
            string entryAssemblyPath,
            ISharedAssemblyCatalog sharedAssemblies,
            IReadOnlyDictionary<string, Assembly> platformAssemblies)
            : base($"Tesseris.Mod:{package.Id}:{Guid.NewGuid():N}", isCollectible: true)
        {
            this.package = package;
            resolver = new AssemblyDependencyResolver(entryAssemblyPath);
            this.sharedAssemblies = sharedAssemblies;
            this.platformAssemblies = platformAssemblies;
            declaredShared = package.SharedAssemblies.ToHashSet(StringComparer.Ordinal);
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            string name = assemblyName.Name ?? string.Empty;
            if (platformAssemblies.TryGetValue(name, out Assembly? platform)) return platform;
            if (declaredShared.Contains(name))
            {
                if (!sharedAssemblies.TryResolve(assemblyName, out Assembly? shared) || shared is null)
                    throw new LoaderException(
                        $"Mod '{package.Id}' declared shared assembly '{name}', but the loader did not negotiate it.");
                AssemblyName actual = shared.GetName();
                if (!AssemblyName.ReferenceMatchesDefinition(assemblyName, actual))
                    throw new LoaderException(
                        $"Mod '{package.Id}' requested shared assembly '{assemblyName}', but loader supplied '{actual}'.");
                return shared;
            }

            string? path = resolver.ResolveAssemblyToPath(assemblyName);
            if (path is null) return null;
            string safe = ResolveSafePackagePath(package.Id, package.Directory, path);
            using FileStream assemblyStream = new(safe, FileMode.Open, FileAccess.Read, FileShare.Read);
            string symbolsPath = Path.ChangeExtension(safe, ".pdb");
            if (!File.Exists(symbolsPath)) return LoadFromStream(assemblyStream);
            using FileStream symbolsStream = new(symbolsPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return LoadFromStream(assemblyStream, symbolsStream);
        }

        protected override nint LoadUnmanagedDll(string unmanagedDllName)
        {
            string? path = resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            if (path is null) return nint.Zero;
            string safe = ResolveSafePackagePath(package.Id, package.Directory, path);
            return LoadUnmanagedDllFromPath(safe);
        }
    }
}

public sealed class ModAssemblyLoadHandle : IDisposable
{
    private Assembly? assembly;
    private Type? entryType;
    private AssemblyLoadContext? context;

    internal ModAssemblyLoadHandle(
        string modId,
        ModEntrypointDescriptor entrypoint,
        Assembly assembly,
        Type entryType,
        AssemblyLoadContext context)
    {
        ModId = modId;
        Entrypoint = entrypoint;
        this.assembly = assembly;
        this.entryType = entryType;
        this.context = context;
        LoadContextReference = new WeakReference(context);
    }

    public string ModId { get; }

    public ModEntrypointDescriptor Entrypoint { get; }

    public Assembly Assembly => assembly ?? throw new ObjectDisposedException(nameof(ModAssemblyLoadHandle));

    public Type EntryType => entryType ?? throw new ObjectDisposedException(nameof(ModAssemblyLoadHandle));

    /// <summary>Weak reference used to prove collectible unload after this handle and all mod objects are released.</summary>
    public WeakReference LoadContextReference { get; }

    public TContract CreateInstance<TContract>() where TContract : class
    {
        Type type = EntryType;
        if (!type.IsPublic || type.IsAbstract || type.ContainsGenericParameters
            || type.GetConstructor(Type.EmptyTypes) is null || !typeof(TContract).IsAssignableFrom(type))
        {
            throw new LoaderException(
                $"Mod '{ModId}' entry type '{type.FullName}' must be a public concrete {typeof(TContract).FullName} "
                + "with a public parameterless constructor.");
        }
        try
        {
            return (TContract)Activator.CreateInstance(type)!;
        }
        catch (Exception exception)
        {
            throw new LoaderException($"Mod '{ModId}' entry type '{type.FullName}' failed to construct.", exception);
        }
    }

    public void Dispose()
    {
        AssemblyLoadContext? unload = context;
        context = null;
        entryType = null;
        assembly = null;
        unload?.Unload();
        GC.SuppressFinalize(this);
    }
}
