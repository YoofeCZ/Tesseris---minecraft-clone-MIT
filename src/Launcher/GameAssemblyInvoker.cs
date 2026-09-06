using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.Loader;
using Tesseris.Loader.Abstractions;
using Tesseris.ModApi;

namespace Tesseris.Launcher;

internal static class GameAssemblyInvoker
{
    public static int Invoke(
        string transformedPath,
        string originalPath,
        string typeName,
        string methodName,
        string[] args,
        IReadOnlyDictionary<string, Assembly> patchAssemblies,
        ILauncherObserver? observer)
    {
        using var context = new GameLoadContext(originalPath, patchAssemblies);
        observer?.OnPhase(new(LauncherPhase.BeforeGameLoad, AssemblyPath: transformedPath));
        Assembly gameAssembly = context.LoadFromAssemblyPath(Path.GetFullPath(transformedPath));
        observer?.OnPhase(new(LauncherPhase.GameLoaded, AssemblyPath: transformedPath));
        Type type = gameAssembly.GetType(typeName, throwOnError: false, ignoreCase: false)
            ?? throw new LauncherBootstrapException(
                $"Game entry type '{typeName}' was not found in transformed assembly '{transformedPath}'.");
        MethodInfo? method = type.GetMethod(
            methodName,
            BindingFlags.Public | BindingFlags.Static,
            binder: null,
            types: new[] { typeof(string[]) },
            modifiers: null);
        if (method is null || method.ReturnType != typeof(int))
            throw new LauncherBootstrapException(
                $"Game entry '{typeName}.{methodName}' must be public static int {methodName}(string[] args).");
        try
        {
            return (int)method.Invoke(null, new object[] { args })!;
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }

    private sealed class GameLoadContext : AssemblyLoadContext, IDisposable
    {
        private readonly AssemblyDependencyResolver resolver;
        private readonly IReadOnlyDictionary<string, Assembly> patchAssemblies;
        private readonly IReadOnlyDictionary<string, Assembly> platformAssemblies;

        public GameLoadContext(string originalPath, IReadOnlyDictionary<string, Assembly> patchAssemblies)
            : base($"Tesseris.Game:{Guid.NewGuid():N}", isCollectible: true)
        {
            resolver = new AssemblyDependencyResolver(Path.GetFullPath(originalPath));
            this.patchAssemblies = patchAssemblies;
            // A normal game launch crosses no public contract objects, only Main(string[]). Loading
            // ModApi/Loader.Abstractions locally keeps every game and runtime-mod type in one ALC.
            // Coremod patch assemblies are the sole case that needs the launcher's shared identities.
            platformAssemblies = patchAssemblies.Count == 0
                ? new Dictionary<string, Assembly>(StringComparer.Ordinal)
                : new Dictionary<string, Assembly>(StringComparer.Ordinal)
                {
                    [typeof(ResourceId).Assembly.GetName().Name!] = typeof(ResourceId).Assembly,
                    [typeof(IPreLaunchMod).Assembly.GetName().Name!] = typeof(IPreLaunchMod).Assembly,
                };
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            string name = assemblyName.Name ?? string.Empty;
            if (patchAssemblies.TryGetValue(name, out Assembly? patch)) return patch;
            if (platformAssemblies.TryGetValue(name, out Assembly? platform)) return platform;
            string? path = resolver.ResolveAssemblyToPath(assemblyName);
            return path is null ? null : LoadFromAssemblyPath(path);
        }

        protected override nint LoadUnmanagedDll(string unmanagedDllName)
        {
            string? path = resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            return path is null ? nint.Zero : LoadUnmanagedDllFromPath(path);
        }

        public void Dispose() => Unload();
    }
}
