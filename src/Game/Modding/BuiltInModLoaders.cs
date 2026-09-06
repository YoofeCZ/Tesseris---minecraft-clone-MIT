using System.Reflection;
using System.Runtime.Loader;
using Tesseris.ModApi;

namespace Tesseris.Game.Modding;

public sealed class ContentModLoader : IModLoader
{
    public string Id => "tesseris:content";

    public bool CanLoad(ModDescriptor descriptor) => descriptor.EntryAssembly is null && descriptor.ContentRoot is not null;

    public IModLoadResult Load(ModDescriptor descriptor, IModLoadContext context)
    {
        _ = context;
        string root = ModPaths.ResolveInside(descriptor.Directory, descriptor.ContentRoot!);
        if (!Directory.Exists(root)) throw new ModHostException($"Content root for '{descriptor.Id}' does not exist: {root}");
        return new BasicModLoadResult(null, new[] { new ModContentSource(descriptor.Id, root) });
    }
}

public sealed class ManagedModLoader : IModLoader
{
    public string Id => "tesseris:managed";

    public bool CanLoad(ModDescriptor descriptor) => descriptor.EntryAssembly is not null;

    public IModLoadResult Load(ModDescriptor descriptor, IModLoadContext context)
    {
        _ = context;
        if (!descriptor.TrustedCode)
        {
            throw new ModHostException(
                $"Mod '{descriptor.Id}' contains managed code but does not explicitly set trustedCode to true.");
        }

        string assemblyPath = ModPaths.ResolveInside(descriptor.Directory, descriptor.EntryAssembly!);
        if (!File.Exists(assemblyPath)) throw new ModHostException($"Entry assembly for '{descriptor.Id}' does not exist: {assemblyPath}");

        var loadContext = new ModAssemblyLoadContext(assemblyPath);
        try
        {
            Assembly assembly = loadContext.LoadFromAssemblyPath(assemblyPath);
            Type entryType = assembly.GetType(descriptor.EntryType!, throwOnError: true, ignoreCase: false)!;
            if (!typeof(IMod).IsAssignableFrom(entryType) || entryType.IsAbstract || entryType.GetConstructor(Type.EmptyTypes) is null)
            {
                throw new ModHostException(
                    $"Entry type '{descriptor.EntryType}' for '{descriptor.Id}' must be a concrete IMod with a public parameterless constructor.");
            }

            var instance = (IMod)Activator.CreateInstance(entryType)!;
            var sources = new List<ModContentSource>();
            if (descriptor.ContentRoot is not null)
            {
                string root = ModPaths.ResolveInside(descriptor.Directory, descriptor.ContentRoot);
                if (!Directory.Exists(root)) throw new ModHostException($"Content root for '{descriptor.Id}' does not exist: {root}");
                sources.Add(new ModContentSource(descriptor.Id, root));
            }

            return new ManagedModLoadResult(instance, sources, loadContext);
        }
        catch
        {
            loadContext.Unload();
            throw;
        }
    }

    private sealed class ModAssemblyLoadContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver resolver;

        public ModAssemblyLoadContext(string entryAssemblyPath)
            : base($"Tesseris.Mod:{Path.GetFileNameWithoutExtension(entryAssemblyPath)}", isCollectible: true)
        {
            resolver = new AssemblyDependencyResolver(entryAssemblyPath);
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (string.Equals(assemblyName.Name, typeof(IMod).Assembly.GetName().Name, StringComparison.Ordinal)) return null;
            string? path = resolver.ResolveAssemblyToPath(assemblyName);
            return path is null ? null : LoadFromAssemblyPath(path);
        }

        protected override nint LoadUnmanagedDll(string unmanagedDllName)
        {
            string? path = resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            return path is null ? nint.Zero : LoadUnmanagedDllFromPath(path);
        }
    }
}

internal sealed class BasicModLoadResult : IModLoadResult
{
    public BasicModLoadResult(IMod? instance, IReadOnlyList<ModContentSource> contentSources)
    {
        Instance = instance;
        ContentSources = contentSources;
    }

    public IMod? Instance { get; }
    public IReadOnlyList<ModContentSource> ContentSources { get; }
    public void Dispose() { }
}

internal sealed class ManagedModLoadResult : IModLoadResult
{
    private AssemblyLoadContext? loadContext;

    public ManagedModLoadResult(IMod instance, IReadOnlyList<ModContentSource> contentSources, AssemblyLoadContext loadContext)
    {
        Instance = instance;
        ContentSources = contentSources;
        this.loadContext = loadContext;
    }

    public IMod? Instance { get; private set; }
    public IReadOnlyList<ModContentSource> ContentSources { get; }

    public void Dispose()
    {
        Instance = null;
        loadContext?.Unload();
        loadContext = null;
    }
}

internal static class ModPaths
{
    public static string ResolveInside(string directory, string relativePath)
    {
        if (Path.IsPathRooted(relativePath)) throw new ModHostException($"Mod path must be relative: {relativePath}");
        string basePath = Path.GetFullPath(directory);
        string result = Path.GetFullPath(Path.Combine(basePath, relativePath));
        string prefix = Path.EndsInDirectorySeparator(basePath) ? basePath : basePath + Path.DirectorySeparatorChar;
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!result.StartsWith(prefix, comparison)) throw new ModHostException($"Mod path escapes its directory: {relativePath}");
        return result;
    }
}
