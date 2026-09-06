using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tesseris.Loader;
using Tesseris.Loader.Abstractions;
using Xunit;

namespace Tesseris.Tests;

public sealed class ModArchiveDiscoveryTests
{
    [Fact]
    public void Sdk_style_vmod_is_discovered_recursively_and_extracted_for_runtime_use()
    {
        using var temporary = new LoaderTemporaryDirectory();
        string nested = Path.Combine(temporary.Path, "downloads", "release");
        Directory.CreateDirectory(nested);
        string archive = Path.Combine(nested, "archive-mod-1.0.0.vmod");
        CreateArchive(archive, "archive_mod", "first");

        DiscoveredModPackage package = Assert.Single(ModManifestDiscovery.Discover(temporary.Path));

        Assert.Equal("archive_mod", package.Package.Id);
        Assert.Equal(Path.GetFullPath(archive), package.ManifestPath);
        Assert.True(File.Exists(Path.Combine(package.Package.Directory, "ArchiveMod.dll")));
        Assert.Equal("first", File.ReadAllText(Path.Combine(package.Package.Directory, "content", "value.txt")));
        Assert.Equal(
            Path.Combine(package.Package.Directory, "content"),
            Assert.Single(package.Package.ContentSources).RootPath);
        Assert.StartsWith(
            Path.Combine(temporary.Path, ".tesseris", "packages"),
            package.Package.Directory,
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("../escaped.txt")]
    [InlineData("nested/../../escaped.txt")]
    [InlineData("/rooted.txt")]
    [InlineData("C:/rooted.txt")]
    [InlineData("content/file.txt:stream")]
    [InlineData("content/nul.txt")]
    public void Unsafe_archive_paths_are_rejected_without_writing_outside_cache(string unsafePath)
    {
        using var temporary = new LoaderTemporaryDirectory();
        string archive = Path.Combine(temporary.Path, "unsafe.vmod");
        CreateRawArchive(archive,
        [
            ("tesseris.mod.json", Manifest("unsafe", "value")),
            (unsafePath, "owned"),
        ]);

        LoaderException failure = Assert.Throws<LoaderException>(() =>
            ModManifestDiscovery.Discover(temporary.Path));

        Assert.Contains("archive", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(temporary.Path, "escaped.txt")));
        Assert.False(File.Exists(Path.Combine(Path.GetPathRoot(temporary.Path)!, "rooted.txt")));
    }

    [Fact]
    public void Content_addressed_cache_is_reused_and_archive_sha_change_invalidates_it()
    {
        using var temporary = new LoaderTemporaryDirectory();
        string archive = Path.Combine(temporary.Path, "cached.vmod");
        CreateArchive(archive, "cached", "one");
        DiscoveredModPackage first = Assert.Single(ModManifestDiscovery.Discover(temporary.Path));
        string expectedFirstHash = Sha256(archive);
        string manifest = Path.Combine(first.Package.Directory, ModManifestDiscovery.ManifestFileName);
        DateTime stableTimestamp = new(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(manifest, stableTimestamp);

        DiscoveredModPackage reused = Assert.Single(ModManifestDiscovery.Discover(temporary.Path));

        Assert.Equal(first.Package.Directory, reused.Package.Directory);
        Assert.Equal(stableTimestamp, File.GetLastWriteTimeUtc(manifest));
        Assert.EndsWith(expectedFirstHash, first.Package.Directory, StringComparison.Ordinal);
        Assert.Equal(expectedFirstHash, first.Package.PackageHash);
        Assert.Equal(first.Package.PackageHash, reused.Package.PackageHash);

        File.Delete(archive);
        CreateArchive(archive, "cached", "two");
        DiscoveredModPackage changed = Assert.Single(ModManifestDiscovery.Discover(temporary.Path));

        Assert.NotEqual(first.Package.Directory, changed.Package.Directory);
        Assert.EndsWith(Sha256(archive), changed.Package.Directory, StringComparison.Ordinal);
        Assert.Equal("two", File.ReadAllText(Path.Combine(changed.Package.Directory, "content", "value.txt")));
    }

    [Fact]
    public void Caller_provided_cache_is_used_and_not_rediscovered_when_nested_under_mods()
    {
        using var temporary = new LoaderTemporaryDirectory();
        string archive = Path.Combine(temporary.Path, "custom-cache.vmod");
        string cache = Path.Combine(temporary.Path, "private", "cache");
        CreateArchive(archive, "custom_cache", "content");
        var options = new ModPackageDiscoveryOptions(CacheRoot: cache);

        DiscoveredModPackage first = Assert.Single(ModManifestDiscovery.Discover(temporary.Path, options));
        IReadOnlyList<DiscoveredModPackage> second = ModManifestDiscovery.Discover(temporary.Path, options);

        Assert.StartsWith(cache, first.Package.Directory, StringComparison.OrdinalIgnoreCase);
        Assert.Single(second);
        Assert.Equal("custom_cache", second[0].Package.Id);
    }

    [Fact]
    public void Canonical_and_legacy_package_caches_are_never_discovered_as_mods()
    {
        using var temporary = new LoaderTemporaryDirectory();
        temporary.WriteContentOnly("installed");

        foreach (string cacheName in new[] { ".tesseris", ".voxelity" })
        {
            string cached = Path.Combine(temporary.Path, cacheName, "packages", "stale");
            Directory.CreateDirectory(cached);
            File.WriteAllText(
                Path.Combine(cached, "voxelity.mod.json"),
                Manifest("stale_duplicate", cacheName));
        }

        DiscoveredModPackage package = Assert.Single(ModManifestDiscovery.Discover(temporary.Path));

        Assert.Equal("installed", package.Package.Id);
    }

    [Fact]
    public void Entry_count_and_expanded_size_limits_fail_closed()
    {
        using var temporary = new LoaderTemporaryDirectory();
        string countArchive = Path.Combine(temporary.Path, "count.vmod");
        CreateRawArchive(countArchive,
        [
            ("tesseris.mod.json", Manifest("count", "value")),
            ("content/value.txt", "value"),
            ("extra.txt", "extra"),
        ]);
        var countOptions = new ModPackageDiscoveryOptions(MaximumArchiveEntries: 2);
        LoaderException countFailure = Assert.Throws<LoaderException>(() =>
            ModManifestDiscovery.Discover(temporary.Path, countOptions));
        Assert.Contains("limit is 2", countFailure.Message, StringComparison.Ordinal);

        File.Delete(countArchive);
        string sizeArchive = Path.Combine(temporary.Path, "size.vmod");
        CreateRawArchive(sizeArchive,
        [
            ("tesseris.mod.json", Manifest("size", "value")),
            ("content/value.txt", new string('x', 128)),
        ]);
        var sizeOptions = new ModPackageDiscoveryOptions(MaximumEntryBytes: 64, MaximumExpandedBytes: 1024);
        LoaderException sizeFailure = Assert.Throws<LoaderException>(() =>
            ModManifestDiscovery.Discover(temporary.Path, sizeOptions));
        Assert.Contains("per-entry limit is 64", sizeFailure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unpacked_packages_remain_compatible_alongside_archives()
    {
        using var temporary = new LoaderTemporaryDirectory();
        temporary.WriteContentOnly("unpacked");
        CreateArchive(Path.Combine(temporary.Path, "archive.vmod"), "archive", "value");

        IReadOnlyList<DiscoveredModPackage> packages = ModManifestDiscovery.Discover(temporary.Path);

        Assert.Equal(["archive", "unpacked"], packages.Select(item => item.Package.Id).Order(StringComparer.Ordinal));
        Assert.Contains(packages, item => item.ManifestPath.EndsWith(".vmod", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(packages, item => item.ManifestPath.EndsWith("tesseris.mod.json", StringComparison.Ordinal));
    }

    [Fact]
    public void Archive_and_unpacked_duplicate_ids_keep_existing_resolver_failure()
    {
        using var temporary = new LoaderTemporaryDirectory();
        temporary.WriteContentOnly("duplicate");
        CreateArchive(Path.Combine(temporary.Path, "duplicate.vmod"), "duplicate", "value");
        IReadOnlyList<DiscoveredModPackage> packages = ModManifestDiscovery.Discover(temporary.Path);

        LoaderException failure = Assert.Throws<LoaderException>(() =>
            ModDependencyResolver.Resolve(packages, LoaderCompatibilityOptions.Create("1.0.0")));

        Assert.Contains("Duplicate mod ID 'duplicate'", failure.Message, StringComparison.Ordinal);
        Assert.Contains("duplicate.vmod", failure.Message, StringComparison.Ordinal);
        Assert.Contains("tesseris.mod.json", failure.Message, StringComparison.Ordinal);
    }

    private static void CreateArchive(string path, string id, string value) =>
        CreateRawArchive(path,
        [
            ("ArchiveMod.dll", "not-loaded-by-discovery"),
            ("content/value.txt", value),
            ("tesseris.mod.json", Manifest(id, value, managed: true)),
        ]);

    private static string Manifest(string id, string marker, bool managed = false) => JsonSerializer.Serialize(new
    {
        schemaVersion = 2,
        id,
        name = id,
        version = "1.0.0",
        loaderVersion = "*",
        modApiVersion = "2.0.0",
        trustedCode = managed,
        entrypoints = managed
            ? new[] { new { phase = "Runtime", assembly = "ArchiveMod.dll", type = "ArchiveMod.Entry" } }
            : null,
        contentRoot = "content",
        capabilities = new[] { marker },
    });

    private static void CreateRawArchive(string path, IReadOnlyList<(string Name, string Content)> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using ZipArchive archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach ((string name, string content) in entries)
        {
            ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Optimal);
            entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            writer.Write(content);
        }
    }

    private static string Sha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
