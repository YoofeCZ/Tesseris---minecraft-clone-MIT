using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Tesseris.ModApi;

namespace Tesseris.Game.Modding;

/// <summary>
/// Discovers explicitly trusted mod-loader plugins without making them part of the game build.
/// Keep this object alive while its <see cref="Loaders"/> are used by a <see cref="ModHost"/>,
/// then dispose it after the host so the collectible plugin contexts can unload.
/// </summary>
public sealed class ExternalModLoaderDiscovery : IDisposable
{
    public const string ManifestFileName = "tesseris.loader.json";
    public const string LegacyManifestFileName = "voxelity.loader.json";

    private readonly List<LoaderEntry> entries = new();
    private IReadOnlyList<IModLoader> loaders = Array.Empty<IModLoader>();
    private bool disposed;

    private ExternalModLoaderDiscovery()
    {
    }

    public IReadOnlyList<IModLoader> Loaders => loaders;

    /// <summary>
    /// Recursively discovers loader manifests. A missing directory is treated as an empty loader root.
    /// </summary>
    public static ExternalModLoaderDiscovery Discover(string loadersRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(loadersRoot);
        var discovery = new ExternalModLoaderDiscovery();

        try
        {
            string root = Path.GetFullPath(loadersRoot);
            if (!Directory.Exists(root))
            {
                return discovery;
            }

            var enumeration = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = false,
                ReturnSpecialDirectories = false,
                AttributesToSkip = FileAttributes.ReparsePoint,
            };

            string[] manifestPaths = new[] { ManifestFileName, LegacyManifestFileName }
                .SelectMany(fileName => Directory.EnumerateFiles(root, fileName, enumeration))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(
                    path => NormalizeRelativePath(Path.GetRelativePath(root, path)),
                    StringComparer.Ordinal)
                .ToArray();

            foreach (string manifestPath in manifestPaths)
            {
                discovery.entries.Add(Load(manifestPath));
            }

            Dictionary<string, LoaderEntry> byId = new(StringComparer.Ordinal);
            foreach (LoaderEntry entry in discovery.entries)
            {
                string id;
                try
                {
                    id = entry.Loader.Id;
                }
                catch (Exception exception)
                {
                    throw ManifestError(entry.ManifestPath, "reading the loader ID failed", exception);
                }

                if (string.IsNullOrWhiteSpace(id))
                {
                    throw ManifestError(entry.ManifestPath, "the loader returned an empty ID");
                }

                if (byId.TryGetValue(id, out LoaderEntry? existing))
                {
                    throw ManifestError(
                        entry.ManifestPath,
                        $"loader ID '{id}' duplicates the loader declared by '{existing.ManifestPath}'");
                }

                byId.Add(id, entry);
            }

            discovery.loaders = discovery.entries.Select(entry => entry.Loader).ToArray();
            return discovery;
        }
        catch
        {
            discovery.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        loaders = Array.Empty<IModLoader>();
        for (int index = entries.Count - 1; index >= 0; index--)
        {
            LoaderEntry entry = entries[index];
            if (entry.Loader is IDisposable disposable)
            {
                try
                {
                    disposable.Dispose();
                }
                catch
                {
                    // Plugin cleanup must not prevent the remaining collectible contexts from unloading.
                }
            }

            entry.Context.Unload();
        }

        entries.Clear();
        disposed = true;
        GC.SuppressFinalize(this);
    }

    private static LoaderEntry Load(string manifestPath)
    {
        LoaderAssemblyLoadContext? loadContext = null;
        try
        {
            LoaderManifest manifest = ReadManifest(manifestPath);
            string directory = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
            string assemblyPath = ResolveInside(directory, manifest.Assembly, manifestPath);
            if (!File.Exists(assemblyPath))
            {
                throw ManifestError(manifestPath, $"assembly does not exist: {assemblyPath}");
            }

            loadContext = new LoaderAssemblyLoadContext(assemblyPath);
            Assembly assembly;
            using (FileStream stream = new(assemblyPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                // Loading the entry assembly from a stream avoids pinning the plugin file on Windows.
                // Dependencies still resolve relative to the original assembly path below.
                assembly = loadContext.LoadFromStream(stream);
            }
            Type? entryType = assembly.GetType(manifest.EntryType, throwOnError: false, ignoreCase: false);
            if (entryType is null)
            {
                throw ManifestError(manifestPath, $"entry type '{manifest.EntryType}' was not found");
            }

            bool isPublic = entryType.IsPublic || entryType.IsNestedPublic;
            ConstructorInfo? constructor = entryType.GetConstructor(Type.EmptyTypes);
            if (!isPublic
                || entryType.IsAbstract
                || entryType.IsInterface
                || entryType.ContainsGenericParameters
                || !typeof(IModLoader).IsAssignableFrom(entryType)
                || constructor is null)
            {
                throw ManifestError(
                    manifestPath,
                    $"entry type '{manifest.EntryType}' must be a concrete public IModLoader with a public parameterless constructor");
            }

            IModLoader loader;
            try
            {
                loader = (IModLoader)constructor.Invoke(null);
            }
            catch (Exception exception)
            {
                throw ManifestError(manifestPath, $"constructing entry type '{manifest.EntryType}' failed", exception);
            }

            LoaderEntry result = new(manifestPath, loader, loadContext);
            loadContext = null;
            return result;
        }
        catch (ModHostException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or JsonException
                or BadImageFormatException
                or FileLoadException
                or FileNotFoundException
                or TypeLoadException)
        {
            throw ManifestError(manifestPath, exception.Message, exception);
        }
        finally
        {
            loadContext?.Unload();
        }
    }

    private static LoaderManifest ReadManifest(string manifestPath)
    {
        using JsonDocument document = JsonDocument.Parse(
            File.ReadAllText(manifestPath),
            new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });

        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw ManifestError(manifestPath, "the manifest root must be an object");
        }

        string assembly = RequiredString(root, "assembly", manifestPath);
        string entryType = RequiredString(root, "entryType", manifestPath);
        if (!root.TryGetProperty("trustedCode", out JsonElement trustedCode)
            || trustedCode.ValueKind != JsonValueKind.True)
        {
            throw ManifestError(manifestPath, "trustedCode must explicitly be true");
        }

        return new LoaderManifest(assembly, entryType);
    }

    private static string RequiredString(JsonElement root, string propertyName, string manifestPath)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement property)
            || property.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(property.GetString()))
        {
            throw ManifestError(manifestPath, $"property '{propertyName}' must be a non-empty string");
        }

        return property.GetString()!.Trim();
    }

    private static string ResolveInside(string directory, string relativePath, string manifestPath)
    {
        if (Path.IsPathRooted(relativePath))
        {
            throw ManifestError(manifestPath, $"loader assembly path must be relative: {relativePath}");
        }

        string basePath = Path.GetFullPath(directory);
        string result = Path.GetFullPath(Path.Combine(basePath, relativePath));
        string prefix = Path.EndsInDirectorySeparator(basePath) ? basePath : basePath + Path.DirectorySeparatorChar;
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!result.StartsWith(prefix, comparison))
        {
            throw ManifestError(
                manifestPath,
                $"loader assembly path escapes its manifest directory: {relativePath}");
        }

        string current = basePath;
        foreach (string segment in Path.GetRelativePath(basePath, result)
                     .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                         StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (File.Exists(current) || Directory.Exists(current))
            {
                FileAttributes attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw ManifestError(
                        manifestPath,
                        $"loader assembly path contains a symbolic link or reparse point: {relativePath}");
                }
            }
        }

        return result;
    }

    private static ModHostException ManifestError(string manifestPath, string reason) =>
        new($"Invalid external mod loader manifest '{manifestPath}': {reason}.");

    private static ModHostException ManifestError(string manifestPath, string reason, Exception innerException) =>
        new($"Invalid external mod loader manifest '{manifestPath}': {reason}.", innerException);

    private static string NormalizeRelativePath(string path) => path.Replace('\\', '/');

    private sealed record LoaderManifest(string Assembly, string EntryType);

    private sealed record LoaderEntry(
        string ManifestPath,
        IModLoader Loader,
        LoaderAssemblyLoadContext Context);

    private sealed class LoaderAssemblyLoadContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver resolver;

        public LoaderAssemblyLoadContext(string entryAssemblyPath)
            : base($"Tesseris.ModLoader:{Path.GetFileNameWithoutExtension(entryAssemblyPath)}", isCollectible: true)
        {
            resolver = new AssemblyDependencyResolver(entryAssemblyPath);
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (string.Equals(
                    assemblyName.Name,
                    typeof(IModLoader).Assembly.GetName().Name,
                    StringComparison.Ordinal))
            {
                return typeof(IModLoader).Assembly;
            }

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
