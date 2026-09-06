using System.Text.Json;
using Tesseris.Loader;
using Tesseris.Loader.Abstractions;
using Xunit;

namespace Tesseris.Tests;

public sealed class LoaderManifestTests
{
    [Fact]
    public void Legacy_manifest_is_adapted_without_changing_runtime_entrypoint()
    {
        using var temporary = new LoaderTemporaryDirectory();
        string directory = temporary.CreatePackage("legacy");
        File.WriteAllBytes(Path.Combine(directory, "Legacy.dll"), [0]);
        temporary.WriteManifest("legacy", new
        {
            id = "legacy",
            name = "Legacy",
            version = "1.2.3",
            apiVersion = "^1.0.0",
            entryAssembly = "Legacy.dll",
            entryType = "Legacy.Entry",
            trustedCode = true,
            dependencies = new Dictionary<string, string> { ["base"] = "^2.0.0" },
        });

        DiscoveredModPackage result = ModManifestDiscovery.Read(Path.Combine(directory, "tesseris.mod.json"));

        Assert.Equal(1, result.Package.SchemaVersion);
        Assert.Equal("*", result.Package.LoaderVersion);
        Assert.Equal("*", result.GameVersionConstraint);
        Assert.Equal(ModTrustLevel.ManagedRuntime, result.Package.Trust);
        ModEntrypointDescriptor entrypoint = Assert.Single(result.Package.Entrypoints);
        Assert.Equal(ModEntrypointPhase.Runtime, entrypoint.Phase);
        Assert.Equal("Legacy.Entry", entrypoint.Type);
        Assert.Equal(ModDependencyKind.Required, Assert.Single(result.Package.Dependencies).Kind);
    }

    [Fact]
    public void V2_manifest_reads_all_dependency_kinds_and_core_trust()
    {
        using var temporary = new LoaderTemporaryDirectory();
        string directory = temporary.CreatePackage("core");
        File.WriteAllBytes(Path.Combine(directory, "Core.dll"), [0]);
        temporary.WriteManifest("core", new
        {
            schemaVersion = 2,
            id = "core",
            name = "Core",
            version = "2.0.0",
            loaderVersion = "^2.0.0",
            modApiVersion = ">=1.0.0 <3.0.0",
            gameVersion = "1.x",
            trustedCode = true,
            coreMod = true,
            entrypoints = new[]
            {
                new { phase = "CoreMod", assembly = "Core.dll", type = "Core.Entry" },
            },
            dependencies = new[] { new { id = "required", version = "1.x", kind = "Required" } },
            optionalDependencies = new Dictionary<string, string> { ["optional"] = "*" },
            conflicts = new Dictionary<string, string> { ["bad"] = ">=1.0.0" },
            loadBefore = new[] { "later" },
            loadAfter = new[] { "earlier" },
            sharedAssemblies = new[] { "Example.Contracts" },
        });

        DiscoveredModPackage result = ModManifestDiscovery.Read(Path.Combine(directory, "tesseris.mod.json"));

        Assert.Equal(ModTrustLevel.CoreMod, result.Package.Trust);
        Assert.Equal("1.x", result.GameVersionConstraint);
        Assert.Equal(5, result.Package.Dependencies.Count);
        Assert.Equal(
            Enum.GetValues<ModDependencyKind>().Order(),
            result.Package.Dependencies.Select(dependency => dependency.Kind).Order());
        Assert.Equal(new[] { "Example.Contracts" }, result.Package.SharedAssemblies);
    }

    [Fact]
    public void Managed_and_core_trust_must_be_explicit()
    {
        using var temporary = new LoaderTemporaryDirectory();
        string managed = temporary.CreatePackage("managed");
        File.WriteAllBytes(Path.Combine(managed, "Managed.dll"), [0]);
        temporary.WriteManifest("managed", new
        {
            id = "managed",
            version = "1.0.0",
            apiVersion = "1.0.0",
            entryAssembly = "Managed.dll",
            entryType = "Managed.Entry",
        });

        LoaderException managedError = Assert.Throws<LoaderException>(() =>
            ModManifestDiscovery.Read(Path.Combine(managed, "tesseris.mod.json")));
        Assert.Contains("trustedCode", managedError.Message, StringComparison.Ordinal);

        string core = temporary.CreatePackage("core");
        File.WriteAllBytes(Path.Combine(core, "Core.dll"), [0]);
        temporary.WriteManifest("core", new
        {
            schemaVersion = 2,
            id = "core",
            version = "1.0.0",
            modApiVersion = "2.0.0",
            loaderVersion = "2.0.0",
            trustedCode = true,
            entrypoints = new[] { new { phase = "CoreMod", assembly = "Core.dll", type = "Core.Entry" } },
        });
        LoaderException coreError = Assert.Throws<LoaderException>(() =>
            ModManifestDiscovery.Read(Path.Combine(core, "tesseris.mod.json")));
        Assert.Contains("coreMod", coreError.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Discovery_is_path_ordered_and_package_hash_changes_with_content()
    {
        using var temporary = new LoaderTemporaryDirectory();
        temporary.WriteContentOnly("z-last");
        string first = temporary.WriteContentOnly("a-first");

        IReadOnlyList<DiscoveredModPackage> before = ModManifestDiscovery.Discover(temporary.Path);
        Assert.Equal(new[] { "a-first", "z-last" }, before.Select(item => item.Package.Id));

        string oldHash = before[0].Package.PackageHash;
        File.WriteAllText(Path.Combine(first, "content", "changed.txt"), "changed");
        string newHash = ModManifestDiscovery.Discover(temporary.Path)[0].Package.PackageHash;
        Assert.NotEqual(oldHash, newHash);
    }

    [Fact]
    public void Package_paths_cannot_escape_their_owner_directory()
    {
        using var temporary = new LoaderTemporaryDirectory();
        string directory = temporary.CreatePackage("escape");
        temporary.WriteManifest("escape", new
        {
            id = "escape",
            version = "1.0.0",
            apiVersion = "1.0.0",
            contentRoot = "../outside",
        });

        LoaderException exception = Assert.Throws<LoaderException>(() =>
            ModManifestDiscovery.Read(Path.Combine(directory, "tesseris.mod.json")));
        Assert.Contains("..", exception.Message, StringComparison.Ordinal);
        Assert.Contains("tesseris.mod.json", exception.Message, StringComparison.Ordinal);
    }
}

internal sealed class LoaderTemporaryDirectory : IDisposable
{
    public LoaderTemporaryDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Tesseris.LoaderTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string CreatePackage(string id)
    {
        string directory = System.IO.Path.Combine(Path, id);
        Directory.CreateDirectory(directory);
        return directory;
    }

    public string WriteContentOnly(string id)
    {
        string directory = CreatePackage(id);
        Directory.CreateDirectory(System.IO.Path.Combine(directory, "content"));
        WriteManifest(id, new
        {
            id,
            version = "1.0.0",
            apiVersion = "1.0.0",
            contentRoot = "content",
        });
        return directory;
    }

    public void WriteManifest(string folder, object manifest)
    {
        string directory = CreatePackage(folder);
        File.WriteAllText(
            System.IO.Path.Combine(directory, "tesseris.mod.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
    }

    public void Dispose()
    {
        if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
    }
}
