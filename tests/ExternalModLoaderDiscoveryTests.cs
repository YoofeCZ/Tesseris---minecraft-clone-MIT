using System.Runtime.Loader;
using System.Text.Json;
using Tesseris.Game.Modding;
using Tesseris.ModApi;
using Xunit;

namespace Tesseris.Tests;

public sealed class ExternalModLoaderDiscoveryTests
{
    [Fact]
    public void Missing_and_empty_roots_return_no_loaders()
    {
        using var temporary = new TemporaryDirectory();
        string missing = Path.Combine(temporary.Path, "missing");

        using ExternalModLoaderDiscovery missingResult = ExternalModLoaderDiscovery.Discover(missing);
        using ExternalModLoaderDiscovery emptyResult = ExternalModLoaderDiscovery.Discover(temporary.Path);

        Assert.Empty(missingResult.Loaders);
        Assert.Empty(emptyResult.Loaders);
    }

    [Fact]
    public void Discovers_recursively_in_deterministic_manifest_path_order_and_shares_mod_api()
    {
        using var temporary = new TemporaryDirectory();
        WriteLoader(temporary.Path, "z-last", typeof(ZetaExternalLoader));
        WriteLoader(temporary.Path, Path.Combine("nested", "a-first"), typeof(AlphaExternalLoader));

        using ExternalModLoaderDiscovery discovery = ExternalModLoaderDiscovery.Discover(temporary.Path);

        Assert.Equal(new[] { "tests:alpha", "tests:zeta" }, discovery.Loaders.Select(loader => loader.Id));
        Assert.All(discovery.Loaders, loader =>
        {
            AssemblyLoadContext? context = AssemblyLoadContext.GetLoadContext(loader.GetType().Assembly);
            Assert.NotNull(context);
            Assert.True(context.IsCollectible);
            Assert.IsAssignableFrom<IModLoader>(loader);
        });
    }

    [Fact]
    public void Dispose_releases_loader_references()
    {
        using var temporary = new TemporaryDirectory();
        WriteLoader(temporary.Path, "loader", typeof(AlphaExternalLoader));
        ExternalModLoaderDiscovery discovery = ExternalModLoaderDiscovery.Discover(temporary.Path);
        Assert.Single(discovery.Loaders);

        discovery.Dispose();

        Assert.Empty(discovery.Loaders);
        discovery.Dispose();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public void Requires_explicit_trust(bool? trustedCode)
    {
        using var temporary = new TemporaryDirectory();
        string directory = CreatePluginDirectory(temporary.Path, "untrusted");
        WriteManifest(directory, "plugin.dll", typeof(AlphaExternalLoader).FullName!, trustedCode);

        ModHostException exception = Assert.Throws<ModHostException>(
            () => ExternalModLoaderDiscovery.Discover(temporary.Path));

        Assert.Contains("tesseris.loader.json", exception.Message, StringComparison.Ordinal);
        Assert.Contains("trustedCode", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_assembly_paths_outside_the_manifest_directory()
    {
        using var temporary = new TemporaryDirectory();
        string directory = Path.Combine(temporary.Path, "nested");
        Directory.CreateDirectory(directory);
        WriteManifest(directory, Path.Combine("..", "plugin.dll"), typeof(AlphaExternalLoader).FullName!, true);

        ModHostException exception = Assert.Throws<ModHostException>(
            () => ExternalModLoaderDiscovery.Discover(temporary.Path));

        Assert.Contains("escapes", exception.Message, StringComparison.Ordinal);
        Assert.Contains("tesseris.loader.json", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_types_that_are_not_public_concrete_parameterless_loaders()
    {
        using var temporary = new TemporaryDirectory();
        WriteLoader(temporary.Path, "invalid", typeof(NotAnExternalLoader));

        ModHostException exception = Assert.Throws<ModHostException>(
            () => ExternalModLoaderDiscovery.Discover(temporary.Path));

        Assert.Contains("concrete public IModLoader", exception.Message, StringComparison.Ordinal);
        Assert.Contains("invalid", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_duplicate_loader_ids_with_both_manifest_paths()
    {
        using var temporary = new TemporaryDirectory();
        WriteLoader(temporary.Path, "one", typeof(AlphaExternalLoader));
        WriteLoader(temporary.Path, "two", typeof(DuplicateAlphaExternalLoader));

        ModHostException exception = Assert.Throws<ModHostException>(
            () => ExternalModLoaderDiscovery.Discover(temporary.Path));

        Assert.Contains("tests:alpha", exception.Message, StringComparison.Ordinal);
        Assert.Contains(Path.Combine("one", ExternalModLoaderDiscovery.ManifestFileName), exception.Message, StringComparison.Ordinal);
        Assert.Contains(Path.Combine("two", ExternalModLoaderDiscovery.ManifestFileName), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Attributes_invalid_json_to_the_manifest()
    {
        using var temporary = new TemporaryDirectory();
        string directory = Path.Combine(temporary.Path, "broken");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, ExternalModLoaderDiscovery.ManifestFileName), "{");

        ModHostException exception = Assert.Throws<ModHostException>(
            () => ExternalModLoaderDiscovery.Discover(temporary.Path));

        Assert.Contains("broken", exception.Message, StringComparison.Ordinal);
        Assert.Contains(ExternalModLoaderDiscovery.ManifestFileName, exception.Message, StringComparison.Ordinal);
        Assert.IsAssignableFrom<JsonException>(exception.InnerException);
    }

    private static void WriteLoader(string root, string relativeDirectory, Type entryType)
    {
        string directory = CreatePluginDirectory(root, relativeDirectory);
        WriteManifest(directory, "plugin.dll", entryType.FullName!, true);
    }

    private static string CreatePluginDirectory(string root, string relativeDirectory)
    {
        string directory = Path.Combine(root, relativeDirectory);
        Directory.CreateDirectory(directory);
        File.Copy(
            typeof(ExternalModLoaderDiscoveryTests).Assembly.Location,
            Path.Combine(directory, "plugin.dll"));
        return directory;
    }

    private static void WriteManifest(string directory, string assembly, string entryType, bool? trustedCode)
    {
        var manifest = new Dictionary<string, object?>
        {
            ["assembly"] = assembly,
            ["entryType"] = entryType,
        };
        if (trustedCode.HasValue)
        {
            manifest["trustedCode"] = trustedCode.Value;
        }

        File.WriteAllText(
            Path.Combine(directory, ExternalModLoaderDiscovery.ManifestFileName),
            JsonSerializer.Serialize(manifest));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "Tesseris.ExternalModLoaderDiscoveryTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}

public sealed class AlphaExternalLoader : ExternalLoaderBase
{
    public override string Id => "tests:alpha";
}

public sealed class DuplicateAlphaExternalLoader : ExternalLoaderBase
{
    public override string Id => "tests:alpha";
}

public sealed class ZetaExternalLoader : ExternalLoaderBase
{
    public override string Id => "tests:zeta";
}

public abstract class ExternalLoaderBase : IModLoader
{
    public abstract string Id { get; }

    public bool CanLoad(ModDescriptor descriptor)
    {
        _ = descriptor;
        return false;
    }

    public IModLoadResult Load(ModDescriptor descriptor, IModLoadContext context)
    {
        _ = descriptor;
        _ = context;
        throw new NotSupportedException();
    }
}

public sealed class NotAnExternalLoader
{
}
