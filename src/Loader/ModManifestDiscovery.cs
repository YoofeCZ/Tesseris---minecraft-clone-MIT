using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tesseris.Loader.Abstractions;
using Tesseris.ModApi;

namespace Tesseris.Loader;

/// <summary>Validated package plus loader-only constraints and diagnostic source path.</summary>
public sealed record DiscoveredModPackage(
    ModPackageDescriptor Package,
    string GameVersionConstraint,
    string ManifestPath);

public static class ModManifestDiscovery
{
    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public const string CanonicalManifestFileName = "tesseris.mod.json";
    public const string LegacyManifestFileName = "voxelity.mod.json";
    public const string ManifestFileName = CanonicalManifestFileName;
    public const string CanonicalArchiveExtension = ".tmod";
    public const string LegacyArchiveExtension = ".vmod";
    public const string CanonicalCacheDirectoryName = ".tesseris";
    public const string LegacyCacheDirectoryName = ".voxelity";

    public static IReadOnlyList<DiscoveredModPackage> Discover(string modsRoot) =>
        Discover(modsRoot, new ModPackageDiscoveryOptions());

    public static IReadOnlyList<DiscoveredModPackage> Discover(
        string modsRoot,
        ModPackageDiscoveryOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modsRoot);
        ArgumentNullException.ThrowIfNull(options);
        string root = Path.GetFullPath(modsRoot);
        if (!Directory.Exists(root)) return Array.Empty<DiscoveredModPackage>();

        string cache = Path.GetFullPath(options.CacheRoot ?? Path.Combine(root, CanonicalCacheDirectoryName, "packages"));
        string canonicalCache = Path.GetFullPath(Path.Combine(root, CanonicalCacheDirectoryName, "packages"));
        string legacyCache = Path.GetFullPath(Path.Combine(root, LegacyCacheDirectoryName, "packages"));

        RejectReparsePoint(root, root);
        var enumerationOptions = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = false,
            ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };

        string[] packagePaths = Directory.EnumerateFiles(root, "*", enumerationOptions)
            .Where(path => !IsInsideOrEqual(path, cache))
            .Where(path => !IsInsideOrEqual(path, canonicalCache))
            .Where(path => !IsInsideOrEqual(path, legacyCache))
            .Where(IsPackagePath)
            .OrderBy(path => Normalize(Path.GetRelativePath(root, path)), StringComparer.Ordinal)
            .ToArray();
        EnsureNoAmbiguousManifestDirectories(packagePaths);

        return packagePaths
            .Select(path => IsArchivePath(path)
                ? ReadArchive(path, cache, options)
                : Read(path))
            .ToArray();
    }

    private static DiscoveredModPackage ReadArchive(
        string archivePath,
        string cacheRoot,
        ModPackageDiscoveryOptions options)
    {
        ExtractedModArchive extracted = ModArchivePackageExtractor.Extract(archivePath, cacheRoot, options);
        string source = Path.GetFullPath(archivePath);
        try
        {
            DiscoveredModPackage package = Read(extracted.ManifestPath);
            return package with
            {
                Package = package.Package with { PackageHash = extracted.ArchiveSha256 },
                ManifestPath = source,
            };
        }
        catch (LoaderException exception)
        {
            throw new LoaderException(
                $"Invalid Tesseris mod archive '{source}': extracted manifest is invalid.",
                exception);
        }
    }

    public static bool IsManifestFileName(string fileName) =>
        string.Equals(fileName, CanonicalManifestFileName, StringComparison.Ordinal)
        || string.Equals(fileName, LegacyManifestFileName, StringComparison.Ordinal);

    public static bool IsArchivePath(string path)
    {
        string extension = Path.GetExtension(path);
        return string.Equals(extension, CanonicalArchiveExtension, StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, LegacyArchiveExtension, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPackagePath(string path) =>
        IsManifestFileName(Path.GetFileName(path)) || IsArchivePath(path);

    private static void EnsureNoAmbiguousManifestDirectories(IReadOnlyList<string> packagePaths)
    {
        IGrouping<string, string>? ambiguous = packagePaths
            .Where(path => IsManifestFileName(Path.GetFileName(path)))
            .GroupBy(path => Path.GetDirectoryName(path)!, PathComparer)
            .FirstOrDefault(group => group.Count() > 1);
        if (ambiguous is null)
        {
            return;
        }

        throw new LoaderException(
            $"Mod package directory '{ambiguous.Key}' contains both "
            + $"'{CanonicalManifestFileName}' and legacy '{LegacyManifestFileName}'.");
    }

    public static DiscoveredModPackage Read(string manifestPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        string fullManifestPath = Path.GetFullPath(manifestPath);
        string directory = Path.GetDirectoryName(fullManifestPath)!;
        try
        {
            RejectReparsePoint(directory, fullManifestPath);
            using JsonDocument document = JsonDocument.Parse(
                File.ReadAllText(fullManifestPath),
                new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw Error(fullManifestPath, "root must be an object");

            int schema = OptionalInt(root, "schemaVersion") ?? 1;
            if (schema is not (1 or 2)) throw Error(fullManifestPath, $"unsupported schemaVersion {schema}");

            string id = RequiredString(root, "id").ToLowerInvariant();
            ValidateModId(id, fullManifestPath);
            string name = OptionalString(root, "name") ?? id;
            string version = SemanticVersion.Parse(RequiredString(root, "version")).ToString();
            string apiConstraint = OptionalString(root, "modApiVersion")
                ?? OptionalString(root, "apiVersion")
                ?? throw Error(fullManifestPath, "property 'modApiVersion' or legacy 'apiVersion' is required");
            _ = VersionConstraint.Parse(apiConstraint);
            string loaderConstraint = OptionalString(root, "loaderVersion") ?? "*";
            string gameConstraint = OptionalString(root, "gameVersion") ?? "*";
            _ = VersionConstraint.Parse(loaderConstraint);
            _ = VersionConstraint.Parse(gameConstraint);

            IReadOnlyList<ModEntrypointDescriptor> entrypoints = ReadEntrypoints(root, schema, fullManifestPath);
            ModTrustLevel trust = DetermineTrust(root, entrypoints, fullManifestPath);
            IReadOnlyList<ModPackageDependency> dependencies = ReadDependencies(root, id, fullManifestPath);
            IReadOnlyList<string> sharedAssemblies = ReadStrings(root, "sharedAssemblies");
            IReadOnlyList<string> capabilities = ReadStrings(root, "capabilities");
            IReadOnlyList<ModContentSource> content = ReadContentSources(root, id, directory, fullManifestPath);

            var package = new ModPackageDescriptor(
                schema,
                id,
                name,
                version,
                loaderConstraint,
                apiConstraint,
                directory,
                trust,
                entrypoints,
                dependencies,
                sharedAssemblies,
                content,
                capabilities,
                ComputePackageHash(directory, fullManifestPath));
            return new DiscoveredModPackage(package, gameConstraint, fullManifestPath);
        }
        catch (LoaderException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or JsonException
                                           or FormatException
                                           or CryptographicException)
        {
            throw Error(fullManifestPath, exception.Message, exception);
        }
    }

    private static IReadOnlyList<ModEntrypointDescriptor> ReadEntrypoints(
        JsonElement root,
        int schema,
        string manifestPath)
    {
        var result = new List<ModEntrypointDescriptor>();
        if (root.TryGetProperty("entrypoints", out JsonElement entrypoints))
        {
            if (entrypoints.ValueKind != JsonValueKind.Array)
                throw Error(manifestPath, "entrypoints must be an array");
            foreach (JsonElement item in entrypoints.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) throw Error(manifestPath, "entrypoints must contain objects");
                if (!Enum.TryParse(RequiredString(item, "phase"), ignoreCase: true, out ModEntrypointPhase phase))
                    throw Error(manifestPath, $"unknown entrypoint phase '{RequiredString(item, "phase")}'");
                result.Add(new ModEntrypointDescriptor(
                    phase,
                    RequiredRelativePath(item, "assembly", manifestPath),
                    RequiredString(item, "type")));
            }
        }

        string? legacyAssembly = OptionalString(root, "entryAssembly");
        string? legacyType = OptionalString(root, "entryType");
        if ((legacyAssembly is null) != (legacyType is null))
            throw Error(manifestPath, "entryAssembly and entryType must both be present or both be omitted");
        if (legacyAssembly is not null)
        {
            if (result.Any(entrypoint => entrypoint.Phase == ModEntrypointPhase.Runtime))
                throw Error(manifestPath, "runtime entrypoint is declared twice");
            result.Add(new ModEntrypointDescriptor(
                ModEntrypointPhase.Runtime,
                ValidateRelativePath(legacyAssembly, manifestPath),
                legacyType!));
        }

        if (schema == 2 && result.Count == 0 && OptionalString(root, "contentRoot") is null
            && !root.TryGetProperty("contentRoots", out _))
        {
            throw Error(manifestPath, "a v2 package must declare an entrypoint or content root");
        }

        return result
            .OrderBy(entrypoint => entrypoint.Phase)
            .ThenBy(entrypoint => entrypoint.Assembly, StringComparer.Ordinal)
            .ThenBy(entrypoint => entrypoint.Type, StringComparer.Ordinal)
            .ToArray();
    }

    private static ModTrustLevel DetermineTrust(
        JsonElement root,
        IReadOnlyList<ModEntrypointDescriptor> entrypoints,
        string manifestPath)
    {
        bool hasManaged = entrypoints.Count > 0;
        bool trusted = root.TryGetProperty("trustedCode", out JsonElement trustedCode)
            && trustedCode.ValueKind == JsonValueKind.True;
        bool coreMarker = root.TryGetProperty("coreMod", out JsonElement coreMod)
            && coreMod.ValueKind == JsonValueKind.True;
        bool hasCore = entrypoints.Any(entrypoint => entrypoint.Phase == ModEntrypointPhase.CoreMod);
        bool hasPreLaunch = hasCore || entrypoints.Any(entrypoint => entrypoint.Phase == ModEntrypointPhase.PreLaunch);

        if (hasManaged && !trusted)
            throw Error(manifestPath, "managed entrypoints require trustedCode to be explicitly true; this is consent, not a sandbox");
        if (hasCore && !coreMarker)
            throw Error(manifestPath, "a coremod entrypoint requires coreMod to be explicitly true");

        return hasCore
            ? ModTrustLevel.CoreMod
            : hasPreLaunch
                ? ModTrustLevel.PreLaunch
                : hasManaged
                    ? ModTrustLevel.ManagedRuntime
                    : ModTrustLevel.ContentOnly;
    }

    private static IReadOnlyList<ModPackageDependency> ReadDependencies(
        JsonElement root,
        string owner,
        string manifestPath)
    {
        var result = new List<ModPackageDependency>();
        ReadDependencyProperty(root, "dependencies", ModDependencyKind.Required, result, manifestPath);
        ReadDependencyProperty(root, "requiredDependencies", ModDependencyKind.Required, result, manifestPath);
        ReadDependencyProperty(root, "optionalDependencies", ModDependencyKind.Optional, result, manifestPath);
        ReadDependencyProperty(root, "conflicts", ModDependencyKind.Incompatible, result, manifestPath);
        ReadDependencyProperty(root, "loadBefore", ModDependencyKind.LoadBefore, result, manifestPath);
        ReadDependencyProperty(root, "loadAfter", ModDependencyKind.LoadAfter, result, manifestPath);

        foreach (ModPackageDependency dependency in result)
        {
            ValidateModId(dependency.ModId, manifestPath);
            _ = VersionConstraint.Parse(dependency.VersionConstraint);
            if (string.Equals(dependency.ModId, owner, StringComparison.Ordinal))
                throw Error(manifestPath, $"mod '{owner}' cannot declare a {dependency.Kind} dependency on itself");
        }

        if (result.GroupBy(dependency => (dependency.ModId, dependency.Kind)).Any(group => group.Count() > 1))
            throw Error(manifestPath, "the same dependency kind is declared more than once for one mod");

        return result.OrderBy(dependency => dependency.ModId, StringComparer.Ordinal)
            .ThenBy(dependency => dependency.Kind)
            .ToArray();
    }

    private static void ReadDependencyProperty(
        JsonElement root,
        string property,
        ModDependencyKind defaultKind,
        List<ModPackageDependency> result,
        string manifestPath)
    {
        if (!root.TryGetProperty(property, out JsonElement value) || value.ValueKind == JsonValueKind.Null) return;
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty item in value.EnumerateObject())
            {
                string constraint = item.Value.ValueKind == JsonValueKind.String
                    ? item.Value.GetString()!
                    : throw Error(manifestPath, $"{property}.{item.Name} must be a version string");
                result.Add(new ModPackageDependency(item.Name.ToLowerInvariant(), constraint, defaultKind));
            }
            return;
        }

        if (value.ValueKind != JsonValueKind.Array)
            throw Error(manifestPath, $"{property} must be an object or array");
        foreach (JsonElement item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                result.Add(new ModPackageDependency(item.GetString()!.ToLowerInvariant(), "*", defaultKind));
                continue;
            }
            if (item.ValueKind != JsonValueKind.Object) throw Error(manifestPath, $"{property} contains an invalid entry");
            string id = RequiredString(item, "id").ToLowerInvariant();
            string constraint = OptionalString(item, "version") ?? "*";
            ModDependencyKind kind = defaultKind;
            if (item.TryGetProperty("kind", out _)
                && !Enum.TryParse(OptionalString(item, "kind"), ignoreCase: true, out kind))
                throw Error(manifestPath, $"unknown dependency kind '{OptionalString(item, "kind")}'");
            result.Add(new ModPackageDependency(id, constraint, kind));
        }
    }

    private static IReadOnlyList<ModContentSource> ReadContentSources(
        JsonElement root,
        string modId,
        string directory,
        string manifestPath)
    {
        var paths = new List<string>();
        string? single = OptionalString(root, "contentRoot");
        if (single is not null) paths.Add(single);
        if (root.TryGetProperty("contentRoots", out JsonElement roots))
        {
            if (roots.ValueKind != JsonValueKind.Array) throw Error(manifestPath, "contentRoots must be an array");
            paths.AddRange(roots.EnumerateArray().Select(item =>
                item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString())
                    ? item.GetString()!
                    : throw Error(manifestPath, "contentRoots must contain non-empty strings")));
        }

        var result = new List<ModContentSource>();
        foreach (string path in paths.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            string relative = ValidateRelativePath(path, manifestPath);
            string full = ResolveInside(directory, relative, manifestPath);
            if (!Directory.Exists(full)) throw Error(manifestPath, $"content root does not exist: {full}");
            RejectReparsePoint(full, manifestPath);
            result.Add(new ModContentSource(modId, full));
        }
        return result;
    }

    private static string ComputePackageHash(string directory, string manifestPath)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = false,
            ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };
        foreach (string file in Directory.EnumerateFiles(directory, "*", options)
                     .OrderBy(path => Normalize(Path.GetRelativePath(directory, path)), StringComparer.Ordinal))
        {
            RejectReparsePoint(file, manifestPath);
            byte[] name = Encoding.UTF8.GetBytes(Normalize(Path.GetRelativePath(directory, file)));
            hash.AppendData(BitConverter.GetBytes(name.Length));
            hash.AppendData(name);
            using FileStream stream = File.OpenRead(file);
            byte[] buffer = new byte[64 * 1024];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0) hash.AppendData(buffer.AsSpan(0, read));
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    internal static string ResolveInside(string directory, string relativePath, string diagnosticPath)
    {
        string relative = ValidateRelativePath(relativePath, diagnosticPath);
        string root = Path.GetFullPath(directory);
        string result = Path.GetFullPath(Path.Combine(root, relative));
        string prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!result.StartsWith(prefix, comparison)) throw Error(diagnosticPath, $"path escapes package directory: {relativePath}");
        return result;
    }

    private static string RequiredRelativePath(JsonElement root, string property, string manifestPath) =>
        ValidateRelativePath(RequiredString(root, property), manifestPath);

    private static string ValidateRelativePath(string path, string manifestPath)
    {
        if (Path.IsPathRooted(path) || string.IsNullOrWhiteSpace(path))
            throw Error(manifestPath, $"package path must be non-empty and relative: {path}");
        string normalized = path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        if (normalized.Split(Path.DirectorySeparatorChar).Any(part => part == ".."))
            throw Error(manifestPath, $"package path may not contain '..': {path}");
        return normalized;
    }

    private static IReadOnlyList<string> ReadStrings(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out JsonElement value)) return Array.Empty<string>();
        if (value.ValueKind != JsonValueKind.Array) throw new FormatException($"{property} must be an array");
        return value.EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString())
                ? item.GetString()!.Trim()
                : throw new FormatException($"{property} must contain non-empty strings"))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static int? OptionalInt(JsonElement root, string property) =>
        !root.TryGetProperty(property, out JsonElement value) || value.ValueKind == JsonValueKind.Null
            ? null
            : value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int result)
                ? result
                : throw new FormatException($"{property} must be an integer");

    private static string RequiredString(JsonElement root, string property) =>
        OptionalString(root, property) ?? throw new FormatException($"required property '{property}' is missing");

    private static string? OptionalString(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out JsonElement value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw new FormatException($"property '{property}' must be a non-empty string");
        return value.GetString()!.Trim();
    }

    private static void ValidateModId(string id, string manifestPath)
    {
        if (id.Length == 0 || id.Any(character =>
                !((character >= 'a' && character <= 'z') || char.IsDigit(character) || character is '_' or '-' or '.')))
            throw Error(manifestPath, $"invalid mod ID '{id}'");
    }

    private static void RejectReparsePoint(string path, string diagnosticPath)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw Error(diagnosticPath, $"symbolic links/reparse points are not accepted: {path}");
    }

    private static string Normalize(string path) => path.Replace('\\', '/');

    private static bool IsInsideOrEqual(string path, string directory)
    {
        string fullPath = Path.GetFullPath(path);
        if (string.Equals(fullPath, directory, PathComparison)) return true;
        string prefix = Path.EndsInDirectorySeparator(directory)
            ? directory
            : directory + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(prefix, PathComparison);
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static LoaderException Error(string path, string reason) =>
        new($"Invalid mod manifest/package '{path}': {reason}.");

    private static LoaderException Error(string path, string reason, Exception inner) =>
        new($"Invalid mod manifest/package '{path}': {reason}.", inner);
}
