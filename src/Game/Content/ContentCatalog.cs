namespace Tesseris.Game.Content;

/// <summary>
/// Deterministically discovers namespaced resources from one or more content-pack roots.
/// </summary>
public sealed class ContentCatalog
{
    private readonly ContentSource[] _sources;

    public ContentCatalog(IEnumerable<ContentSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);

        _sources = [.. sources.Select(NormalizeSource)
            .OrderBy(source => source.LoadOrder)
            .ThenBy(source => source.Namespace, StringComparer.Ordinal)
            .ThenBy(source => source.Root, PathComparer)];
    }

    public IReadOnlyList<ContentSource> Sources => _sources;

    /// <summary>
    /// Gets files under <c>sourceRoot/category</c>. Modern sources may use subdirectories;
    /// legacy sources retain the old flat-directory behavior.
    /// </summary>
    public IReadOnlyList<ContentFile> GetFiles(string category, string extension = ".json")
    {
        ValidateRelativeSegment(category, nameof(category));
        if (string.IsNullOrWhiteSpace(extension) || extension.IndexOfAny(['/', '\\']) >= 0)
        {
            throw new ArgumentException("Extension must be a filename extension.", nameof(extension));
        }

        if (!extension.StartsWith(".", StringComparison.Ordinal))
        {
            extension = "." + extension;
        }

        List<ContentFile> files = [];
        var ids = new Dictionary<ResourceId, string>();

        foreach (ContentSource source in _sources)
        {
            string directory = ResolvePath(source, category);
            if (!Directory.Exists(directory))
            {
                continue;
            }

            SearchOption search = source.LegacyFlat
                ? SearchOption.TopDirectoryOnly
                : SearchOption.AllDirectories;

            IEnumerable<string> discovered = Directory
                .EnumerateFiles(directory, "*" + extension, search)
                .Select(Path.GetFullPath)
                .OrderBy(path => Path.GetRelativePath(directory, path).Replace('\\', '/'), StringComparer.Ordinal);

            foreach (string path in discovered)
            {
                EnsureWithin(directory, path);

                string relative = Path.GetRelativePath(directory, path).Replace('\\', '/');
                string resourcePath = relative[..^extension.Length];
                var id = new ResourceId(source.Namespace, resourcePath);

                if (ids.TryGetValue(id, out string? previous))
                {
                    throw new InvalidDataException(
                        $"Resource '{id}' is defined more than once: '{previous}' and '{path}'.");
                }

                ids.Add(id, path);
                files.Add(new ContentFile(id, path, relative, source));
            }
        }

        return files;
    }

    /// <summary>Resolves a source-relative path and rejects rooted or escaping paths.</summary>
    public string ResolvePath(ContentSource source, params string[] relativeSegments)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(relativeSegments);

        string root = Path.GetFullPath(source.Root);
        string combined = root;

        foreach (string segment in relativeSegments)
        {
            if (string.IsNullOrWhiteSpace(segment) || Path.IsPathRooted(segment))
            {
                throw new InvalidDataException($"Content path '{segment}' must be source-relative.");
            }

            combined = Path.Combine(combined, segment);
        }

        string resolved = Path.GetFullPath(combined);
        EnsureWithin(root, resolved);
        return resolved;
    }

    private static ContentSource NormalizeSource(ContentSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _ = new ResourceId(source.Namespace, "validation");
        ArgumentException.ThrowIfNullOrWhiteSpace(source.Root);

        return source with { Root = Path.GetFullPath(source.Root) };
    }

    private static void ValidateRelativeSegment(string value, string parameter)
    {
        if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value)
            || value.IndexOfAny(['/', '\\']) >= 0 || value is "." or "..")
        {
            throw new ArgumentException("Content category must be one relative directory name.", parameter);
        }
    }

    private static void EnsureWithin(string root, string path)
    {
        string normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string normalizedPath = Path.GetFullPath(path);
        string prefix = normalizedRoot + Path.DirectorySeparatorChar;

        if (!normalizedPath.StartsWith(prefix, PathComparison))
        {
            throw new InvalidDataException(
                $"Content path '{normalizedPath}' escapes source root '{normalizedRoot}'.");
        }
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
}
