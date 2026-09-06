using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;

namespace Tesseris.Launcher;

internal sealed record ExtractedRuntime(string Directory, string Fingerprint);

/// <summary>
/// Publishes the game payload from the single-file launcher into an immutable, content-addressed
/// cache. The physical game assembly remains available to the coremod transformer without exposing
/// the framework and dependency files in the player's installation directory.
/// </summary>
internal static class RuntimePayloadExtractor
{
    internal const string ResourceName = "Tesseris.RuntimePayload.zip";
    private const string CompletionMarker = ".complete";

    public static bool TryExtractEmbedded(string cacheRoot, out ExtractedRuntime runtime)
    {
        Stream? payload = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
        if (payload is null)
        {
            runtime = null!;
            return false;
        }

        using (payload)
        {
            runtime = Extract(payload, cacheRoot);
            return true;
        }
    }

    internal static ExtractedRuntime Extract(Stream payload, string cacheRoot)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheRoot);
        if (!payload.CanRead || !payload.CanSeek)
            throw new InvalidDataException("The embedded Tesseris runtime payload must be readable and seekable.");

        string root = Path.GetFullPath(cacheRoot);
        Directory.CreateDirectory(root);

        payload.Position = 0;
        string fingerprint = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        payload.Position = 0;

        string destination = Path.Combine(root, fingerprint);
        if (IsComplete(destination, fingerprint))
            return new ExtractedRuntime(destination, fingerprint);

        string staging = Path.Combine(root, $".staging-{fingerprint}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            ExtractArchive(payload, staging);
            string gameAssembly = Path.Combine(staging, "Tesseris.dll");
            if (!File.Exists(gameAssembly))
                throw new InvalidDataException("The embedded runtime payload does not contain Tesseris.dll.");

            File.WriteAllText(Path.Combine(staging, CompletionMarker), fingerprint);
            try
            {
                Directory.Move(staging, destination);
            }
            catch (IOException) when (IsComplete(destination, fingerprint))
            {
                // Another launcher instance published the same immutable payload first.
            }

            if (!IsComplete(destination, fingerprint))
                throw new InvalidDataException("The Tesseris runtime cache was not published completely.");
            return new ExtractedRuntime(destination, fingerprint);
        }
        finally
        {
            if (Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);
        }
    }

    private static void ExtractArchive(Stream payload, string staging)
    {
        string stagingPrefix = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;
        using var archive = new ZipArchive(payload, ZipArchiveMode.Read, leaveOpen: true);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string relative = entry.FullName.Replace('\\', '/');
            if (relative.Length == 0 || relative.StartsWith("/", StringComparison.Ordinal)
                || relative.Contains(':', StringComparison.Ordinal))
                throw new InvalidDataException($"Unsafe runtime payload entry '{entry.FullName}'.");

            string[] segments = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0 || segments.Any(segment => segment is "." or ".."))
                throw new InvalidDataException($"Unsafe runtime payload entry '{entry.FullName}'.");

            string output = Path.GetFullPath(Path.Combine(staging, Path.Combine(segments)));
            if (!output.StartsWith(stagingPrefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Runtime payload entry escaped its cache root: '{entry.FullName}'.");
            if (!paths.Add(output))
                throw new InvalidDataException($"Duplicate runtime payload entry '{entry.FullName}'.");

            bool directory = relative.EndsWith("/", StringComparison.Ordinal);
            if (directory)
            {
                Directory.CreateDirectory(output);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            using Stream input = entry.Open();
            using FileStream target = new(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            input.CopyTo(target);
        }
    }

    private static bool IsComplete(string directory, string fingerprint)
    {
        string marker = Path.Combine(directory, CompletionMarker);
        return File.Exists(Path.Combine(directory, "Tesseris.dll"))
            && File.Exists(marker)
            && string.Equals(File.ReadAllText(marker).Trim(), fingerprint, StringComparison.Ordinal);
    }
}
