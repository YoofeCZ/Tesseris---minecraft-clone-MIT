using Tesseris.Game.Modding;
using Xunit;

namespace Tesseris.Tests;

public sealed class ModManagerStateTests
{
    [Fact]
    public void Disabling_dependency_cascades_and_enabling_dependent_restores_requirements()
    {
        using var temporary = new LoaderTemporaryDirectory();
        temporary.WriteContentOnly("library");
        WriteDependent(temporary, "feature", "library");
        temporary.WriteContentOnly("independent");

        ModManagerState manager = ModManagerState.Load(temporary.Path);
        Assert.All(manager.Mods, mod => Assert.True(mod.Enabled));

        Assert.True(manager.Toggle("library"));
        Assert.False(manager.Mods.Single(mod => mod.Id == "library").Enabled);
        Assert.False(manager.Mods.Single(mod => mod.Id == "feature").Enabled);
        Assert.True(manager.Mods.Single(mod => mod.Id == "independent").Enabled);

        Assert.True(manager.Toggle("feature"));
        Assert.True(manager.Mods.Single(mod => mod.Id == "library").Enabled);
        Assert.True(manager.Mods.Single(mod => mod.Id == "feature").Enabled);
    }

    [Fact]
    public void Saved_selection_is_applied_before_mod_host_loads_packages()
    {
        using var temporary = new LoaderTemporaryDirectory();
        temporary.WriteContentOnly("enabled");
        temporary.WriteContentOnly("disabled");

        ModManagerState manager = ModManagerState.Load(temporary.Path);
        manager.Toggle("disabled");
        manager.Save();

        ModManagerState reloaded = ModManagerState.Load(temporary.Path);
        Assert.Equal(new[] { "enabled" }, reloaded.EnabledModIds);

        using ModHost host = ModHost.DiscoverAndLoadStandalone(
            temporary.Path,
            new ModHostOptions { EnabledModIds = reloaded.EnabledModIds });
        Assert.Equal(new[] { "enabled" }, host.LoadedMods.Select(mod => mod.Descriptor.Id));
    }

    private static void WriteDependent(LoaderTemporaryDirectory temporary, string id, string dependency)
    {
        string directory = temporary.CreatePackage(id);
        Directory.CreateDirectory(Path.Combine(directory, "content"));
        temporary.WriteManifest(id, new
        {
            schemaVersion = 2,
            id,
            name = id,
            version = "1.0.0",
            loaderVersion = "*",
            modApiVersion = "^2.0.0",
            contentRoot = "content",
            dependencies = new Dictionary<string, string> { [dependency] = "*" },
        });
    }
}
