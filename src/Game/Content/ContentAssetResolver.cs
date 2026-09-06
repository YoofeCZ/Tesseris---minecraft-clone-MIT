namespace Tesseris.Game.Content;

/// <summary>
/// Resolves namespaced content asset references to paths inside their owning content source.
/// </summary>
public sealed class ContentAssetResolver
{
    private static readonly string[] SupportedAudioExtensions = [".ogg", ".wav"];
    private readonly ContentCatalog _catalog;
    private readonly Dictionary<string, ContentSource[]> _sourcesByNamespace;
    private readonly ContentSource? _legacySource;

    public ContentAssetResolver(ContentCatalog catalog, ContentSource? legacySource = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        _catalog = catalog;
        ContentSource[] legacySources = [.. catalog.Sources.Where(source => source.LegacyFlat)];
        if (legacySource is null && legacySources.Length == 1)
        {
            legacySource = legacySources[0];
        }
        else if (legacySource is null && legacySources.Length > 1)
        {
            throw new InvalidDataException(
                "The content catalog contains more than one legacy source; select one explicitly.");
        }

        _legacySource = legacySource;

        IEnumerable<ContentSource> resolvableSources = catalog.Sources;
        if (legacySource is not null && !catalog.Sources.Any(source => SameSource(source, legacySource)))
        {
            resolvableSources = resolvableSources.Append(legacySource);
        }

        _sourcesByNamespace = resolvableSources
            .GroupBy(source => source.Namespace, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
    }

    /// <summary>
    /// Returns the expected PNG path for a texture reference, or <see langword="null"/> when
    /// its namespace has no content source. The file does not have to exist: callers use the
    /// candidate path to preserve legacy generated-texture caching.
    /// </summary>
    public string? ResolveTexturePath(string texture)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(texture);

        string reference = StripBandSuffix(texture);
        if (!reference.Contains(':'))
        {
            if (_legacySource is null)
            {
                return null;
            }

            ValidateAssetPath(reference, _legacySource.Namespace);
            return _catalog.ResolvePath(_legacySource, "textures", reference + ".png");
        }

        ResourceId id;
        try
        {
            id = ResourceId.Parse(reference);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException($"Invalid texture reference '{texture}'.", exception);
        }

        if (!_sourcesByNamespace.TryGetValue(id.Namespace, out ContentSource[]? sources))
        {
            return null;
        }

        string[] paths = [.. sources.Select(source =>
            _catalog.ResolvePath(source, "textures", id.Path + ".png"))];
        string[] existing = [.. paths.Where(File.Exists)];

        if (existing.Length > 1)
        {
            throw new InvalidDataException(
                $"Texture '{id}' is defined more than once: {string.Join(", ", existing.Select(path => $"'{path}'"))}.");
        }

        return existing.Length == 1 ? existing[0] : paths[0];
    }

    /// <summary>
    /// Resolves an existing namespaced asset in one catalog category. Duplicate definitions are an
    /// error and unknown namespaces/missing files return null.
    /// </summary>
    public string? ResolveAssetPath(
        Tesseris.ModApi.ResourceId assetId,
        string category,
        IReadOnlyCollection<string>? allowedExtensions = null)
    {
        if (string.IsNullOrWhiteSpace(assetId.Value))
            throw new ArgumentException("A non-default asset ID is required.", nameof(assetId));
        if (string.IsNullOrWhiteSpace(category) || Path.IsPathRooted(category)
            || category.IndexOfAny(['/', '\\']) >= 0 || category is "." or "..")
            throw new ArgumentException("Asset category must be one relative directory name.", nameof(category));

        ResourceId id;
        try
        {
            id = ResourceId.Parse(assetId.Value);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException($"Invalid asset ID '{assetId}'.", exception);
        }

        if (allowedExtensions is not null)
        {
            string extension = Path.GetExtension(id.Path);
            if (!allowedExtensions.Any(value => extension.Equals(value, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException(
                    $"Asset '{assetId}' has unsupported extension '{extension}'. Allowed: "
                    + string.Join(", ", allowedExtensions.Order(StringComparer.Ordinal)) + ".");
        }
        if (!_sourcesByNamespace.TryGetValue(id.Namespace, out ContentSource[]? sources)) return null;

        string[] existing = sources
            .Select(source => (Source: source, Path: _catalog.ResolvePath(source, category, id.Path)))
            .Where(candidate => File.Exists(candidate.Path))
            .Select(candidate =>
            {
                EnsureNoReparseEscape(candidate.Source.Root, candidate.Path);
                return candidate.Path;
            })
            .ToArray();
        if (existing.Length > 1)
            throw new InvalidDataException(
                $"Asset '{assetId}' is defined more than once: "
                + string.Join(", ", existing.Select(path => $"'{path}'")) + ".");
        return existing.Length == 1 ? existing[0] : null;
    }

    /// <summary>Resolves an existing audio asset under <c>audio/</c>; only WAV and OGG are supported.</summary>
    public string? ResolveAudioPath(Tesseris.ModApi.ResourceId assetId) =>
        ResolveAssetPath(assetId, "audio", SupportedAudioExtensions);

    private static string StripBandSuffix(string texture)
    {
        int hash = texture.IndexOf('#', StringComparison.Ordinal);
        if (hash < 0)
        {
            return texture;
        }

        if (hash == 0 || hash != texture.LastIndexOf('#')
            || !int.TryParse(texture[(hash + 1)..], out int band) || band < 0)
        {
            throw new InvalidDataException(
                $"Texture reference '{texture}' has an invalid band suffix; expected '#N'.");
        }

        return texture[..hash];
    }

    private static void ValidateAssetPath(string path, string sourceNamespace)
    {
        try
        {
            _ = new ResourceId(sourceNamespace, path);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException($"Invalid texture path '{path}'.", exception);
        }
    }

    private static bool SameSource(ContentSource left, ContentSource right) =>
        string.Equals(left.Namespace, right.Namespace, StringComparison.Ordinal)
        && string.Equals(
            Path.GetFullPath(left.Root),
            Path.GetFullPath(right.Root),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static void EnsureNoReparseEscape(string root, string path)
    {
        string current = Path.GetFullPath(root);
        var rootInfo = new DirectoryInfo(current);
        if (rootInfo.Exists
            && (rootInfo.LinkTarget is not null || (rootInfo.Attributes & FileAttributes.ReparsePoint) != 0))
            throw new InvalidDataException($"Asset root '{root}' must not be a symbolic link or reparse point.");
        string relative = Path.GetRelativePath(current, Path.GetFullPath(path));
        foreach (string segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);
            FileSystemInfo info = Directory.Exists(current)
                ? new DirectoryInfo(current)
                : new FileInfo(current);
            if (info.Exists && (info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0))
                throw new InvalidDataException($"Asset path '{path}' crosses a symbolic link or reparse point.");
        }
    }
}
