using System.IO.Compression;
using System.Security.Cryptography;

namespace Tesseris.Loader;

public sealed record ModPackageDiscoveryOptions(
    string? CacheRoot = null,
    int MaximumArchiveEntries = 16_384,
    long MaximumArchiveBytes = 512L * 1024 * 1024,
    long MaximumEntryBytes = 256L * 1024 * 1024,
    long MaximumExpandedBytes = 1024L * 1024 * 1024);

internal sealed record ExtractedModArchive(string PackageDirectory, string ManifestPath, string ArchiveSha256);

internal static class ModArchivePackageExtractor
{
    public static ExtractedModArchive Extract(
        string archivePath,
        string cacheRoot,
        ModPackageDiscoveryOptions options)
    {
        ValidateOptions(options);
        string archive = Path.GetFullPath(archivePath);
        string cache = Path.GetFullPath(cacheRoot);
        try
        {
            RejectReparsePoint(archive);
            Directory.CreateDirectory(cache);
            RejectReparseChain(cache);

            using FileStream archiveStream = new(archive, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (archiveStream.Length > options.MaximumArchiveBytes)
            {
                throw Error(
                    archive,
                    $"archive is {archiveStream.Length} bytes; limit is {options.MaximumArchiveBytes}");
            }

            string digest = Convert.ToHexString(SHA256.HashData(archiveStream)).ToLowerInvariant();
            archiveStream.Position = 0;
            string target = ResolveDirectChild(cache, digest);
            if (Directory.Exists(target))
            {
                RejectReparsePoint(target);
                string manifest = FindManifest(target, archive, $"cached package '{target}' is incomplete");
                RejectReparsePoint(manifest);
                return new ExtractedModArchive(target, manifest, digest);
            }

            string staging = ResolveDirectChild(cache, $".{digest}.{Guid.NewGuid():N}.tmp");
            Directory.CreateDirectory(staging);
            try
            {
                ExtractToStaging(archive, archiveStream, staging, options);
                _ = FindManifest(
                    staging,
                    archive,
                    $"archive root contains neither '{ModManifestDiscovery.CanonicalManifestFileName}' "
                    + $"nor legacy '{ModManifestDiscovery.LegacyManifestFileName}'");

                PublishStaging(staging, target);

                string manifest = FindManifest(target, archive, $"published package cache '{target}' is incomplete");
                return new ExtractedModArchive(target, manifest, digest);
            }
            finally
            {
                if (Directory.Exists(staging))
                {
                    Directory.Delete(staging, recursive: true);
                }
            }
        }
        catch (LoaderException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or InvalidDataException
                                           or CryptographicException)
        {
            throw Error(archive, exception.Message, exception);
        }
    }

    private static void PublishStaging(string staging, string target)
    {
        // Windows Defender and other scanners can briefly retain a handle to a newly written DLL.
        // Directory.Move then reports access denied although the directory is writable a moment later.
        const int attempts = 8;
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                Directory.Move(staging, target);
                return;
            }
            catch (IOException) when (Directory.Exists(target))
            {
                // Another process published the same content-addressed package first.
                RejectReparsePoint(target);
                return;
            }
            catch (Exception exception) when (
                attempt < attempts - 1 && exception is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(25 << attempt);
            }
        }
    }

    private static string FindManifest(string packageDirectory, string archive, string missingReason)
    {
        string canonical = Path.Combine(packageDirectory, ModManifestDiscovery.CanonicalManifestFileName);
        string legacy = Path.Combine(packageDirectory, ModManifestDiscovery.LegacyManifestFileName);
        bool hasCanonical = File.Exists(canonical);
        bool hasLegacy = File.Exists(legacy);
        if (hasCanonical && hasLegacy)
        {
            throw Error(
                archive,
                $"package root contains both '{ModManifestDiscovery.CanonicalManifestFileName}' and "
                + $"'{ModManifestDiscovery.LegacyManifestFileName}'");
        }

        if (!hasCanonical && !hasLegacy)
        {
            throw Error(archive, missingReason);
        }

        return hasCanonical ? canonical : legacy;
    }

    private static void ExtractToStaging(
        string archivePath,
        Stream archiveStream,
        string staging,
        ModPackageDiscoveryOptions options)
    {
        using ZipArchive archive = new(archiveStream, ZipArchiveMode.Read, leaveOpen: true);
        if (archive.Entries.Count > options.MaximumArchiveEntries)
        {
            throw Error(
                archivePath,
                $"contains {archive.Entries.Count} entries; limit is {options.MaximumArchiveEntries}");
        }

        long totalLength = 0;
        var paths = new HashSet<string>(PathComparer);
        foreach (ZipArchiveEntry entry in archive.Entries.OrderBy(item => item.FullName, StringComparer.Ordinal))
        {
            string relative = ValidateEntry(entry, archivePath);
            bool directory = entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');
            if (!paths.Add(relative))
            {
                throw Error(archivePath, $"contains duplicate path '{entry.FullName}'");
            }

            if (entry.Length > options.MaximumEntryBytes)
            {
                throw Error(
                    archivePath,
                    $"entry '{entry.FullName}' expands to {entry.Length} bytes; per-entry limit is {options.MaximumEntryBytes}");
            }

            try
            {
                totalLength = checked(totalLength + entry.Length);
            }
            catch (OverflowException exception)
            {
                throw Error(archivePath, "expanded size overflows Int64", exception);
            }

            if (totalLength > options.MaximumExpandedBytes)
            {
                throw Error(
                    archivePath,
                    $"expanded content exceeds {options.MaximumExpandedBytes} bytes");
            }

            string destination = ResolveInside(staging, relative, archivePath);
            if (directory)
            {
                Directory.CreateDirectory(destination);
                continue;
            }

            string? parent = Path.GetDirectoryName(destination);
            if (parent is not null) Directory.CreateDirectory(parent);
            using Stream input = entry.Open();
            using FileStream output = new(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            CopyLimited(input, output, entry, options, archivePath);
        }
    }

    private static string ValidateEntry(ZipArchiveEntry entry, string archivePath)
    {
        if (string.IsNullOrEmpty(entry.FullName))
        {
            throw Error(archivePath, "contains an entry with an empty name");
        }

        if (IsLinkOrReparsePoint(entry))
        {
            throw Error(archivePath, $"entry '{entry.FullName}' is a symbolic link/reparse point");
        }

        string normalized = entry.FullName.Replace('\\', '/');
        if (normalized.StartsWith("/", StringComparison.Ordinal)
            || Path.IsPathRooted(normalized)
            || (normalized.Length >= 2 && char.IsLetter(normalized[0]) && normalized[1] == ':'))
        {
            throw Error(archivePath, $"entry path is rooted: {entry.FullName}");
        }

        string trimmed = normalized.EndsWith("/", StringComparison.Ordinal)
            ? normalized[..^1]
            : normalized;
        string[] parts = trimmed.Split('/');
        if (parts.Length == 0 || parts.Any(part => part.Length == 0 || part is "." or ".."))
        {
            throw Error(archivePath, $"entry path is not canonical and relative: {entry.FullName}");
        }

        foreach (string part in parts)
        {
            ValidatePortableSegment(part, entry.FullName, archivePath);
        }

        return string.Join(Path.DirectorySeparatorChar, parts);
    }

    private static void ValidatePortableSegment(string segment, string entryName, string archivePath)
    {
        if (segment.EndsWith(' ') || segment.EndsWith('.')
            || segment.Any(character => character < 32 || character is ':' or '*' or '?' or '"' or '<' or '>' or '|'))
        {
            throw Error(archivePath, $"entry path is not portable: {entryName}");
        }

        string deviceName = segment.Split('.')[0];
        bool reserved = deviceName.Equals("con", StringComparison.OrdinalIgnoreCase)
            || deviceName.Equals("prn", StringComparison.OrdinalIgnoreCase)
            || deviceName.Equals("aux", StringComparison.OrdinalIgnoreCase)
            || deviceName.Equals("nul", StringComparison.OrdinalIgnoreCase)
            || (deviceName.Length == 4
                && (deviceName.StartsWith("com", StringComparison.OrdinalIgnoreCase)
                    || deviceName.StartsWith("lpt", StringComparison.OrdinalIgnoreCase))
                && deviceName[3] is >= '1' and <= '9');
        if (reserved)
        {
            throw Error(archivePath, $"entry path uses a reserved device name: {entryName}");
        }
    }

    private static bool IsLinkOrReparsePoint(ZipArchiveEntry entry)
    {
        const int UnixFileTypeMask = 0xF000;
        const int UnixSymbolicLink = 0xA000;
        int unixMode = (entry.ExternalAttributes >> 16) & UnixFileTypeMask;
        int windowsAttributes = entry.ExternalAttributes & 0xFFFF;
        return unixMode == UnixSymbolicLink
            || (windowsAttributes & (int)FileAttributes.ReparsePoint) != 0;
    }

    private static void CopyLimited(
        Stream input,
        Stream output,
        ZipArchiveEntry entry,
        ModPackageDiscoveryOptions options,
        string archivePath)
    {
        byte[] buffer = new byte[64 * 1024];
        long written = 0;
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            written = checked(written + read);
            if (written > options.MaximumEntryBytes || written > entry.Length)
            {
                throw Error(archivePath, $"entry '{entry.FullName}' exceeded its declared or configured size");
            }

            output.Write(buffer, 0, read);
        }

        if (written != entry.Length)
        {
            throw Error(
                archivePath,
                $"entry '{entry.FullName}' extracted {written} bytes but declared {entry.Length}");
        }
    }

    private static string ResolveDirectChild(string root, string name)
    {
        string candidate = Path.GetFullPath(Path.Combine(root, name));
        string parent = Path.GetDirectoryName(candidate)!;
        if (!string.Equals(parent, root, PathComparison))
        {
            throw new InvalidOperationException($"Cache child '{name}' escaped '{root}'.");
        }

        return candidate;
    }

    private static string ResolveInside(string root, string relative, string archivePath)
    {
        string candidate = Path.GetFullPath(Path.Combine(root, relative));
        string prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, PathComparison))
        {
            throw Error(archivePath, $"entry path escapes extraction root: {relative}");
        }

        return candidate;
    }

    private static void ValidateOptions(ModPackageDiscoveryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.MaximumArchiveEntries <= 0) throw new ArgumentOutOfRangeException(nameof(options.MaximumArchiveEntries));
        if (options.MaximumArchiveBytes <= 0) throw new ArgumentOutOfRangeException(nameof(options.MaximumArchiveBytes));
        if (options.MaximumEntryBytes <= 0) throw new ArgumentOutOfRangeException(nameof(options.MaximumEntryBytes));
        if (options.MaximumExpandedBytes <= 0) throw new ArgumentOutOfRangeException(nameof(options.MaximumExpandedBytes));
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException($"Symbolic links/reparse points are not accepted: {path}");
        }
    }

    private static void RejectReparseChain(string path)
    {
        DirectoryInfo? current = new(Path.GetFullPath(path));
        while (current is not null)
        {
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException($"Symbolic links/reparse points are not accepted: {current.FullName}");
            }

            current = current.Parent;
        }
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static LoaderException Error(string archive, string reason) =>
        new($"Invalid Tesseris mod archive '{archive}': {reason}.");

    private static LoaderException Error(string archive, string reason, Exception inner) =>
        new($"Invalid Tesseris mod archive '{archive}': {reason}.", inner);
}
