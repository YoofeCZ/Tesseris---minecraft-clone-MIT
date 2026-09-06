using System.Text.Json;
using Tesseris.Game.Modding;
using Tesseris.ModApi;
using Xunit;

namespace Tesseris.Tests;

public sealed class ModHostTests
{
    [Fact]
    public void DiscoverAndLoad_orders_dependencies_then_uses_id_for_ties()
    {
        using var temporary = new TemporaryDirectory();
        WriteContentMod(temporary.Path, "z-last", dependencies: new Dictionary<string, string> { ["core"] = "^1.0.0" });
        WriteContentMod(temporary.Path, "a-first");
        WriteContentMod(temporary.Path, "core");

        using ModHost host = ModHost.DiscoverAndLoad(temporary.Path);

        Assert.Equal(new[] { "a-first", "core", "z-last" }, host.LoadedMods.Select(mod => mod.Descriptor.Id));
        Assert.Equal(new[] { "a-first", "core", "z-last" }, host.ContentSources.Select(source => source.ModId));
    }

    [Fact]
    public void DiscoverAndLoad_rejects_duplicate_ids()
    {
        using var temporary = new TemporaryDirectory();
        WriteContentMod(temporary.Path, "same", folder: "one");
        WriteContentMod(temporary.Path, "same", folder: "two");

        ModHostException exception = Assert.Throws<ModHostException>(() => ModHost.DiscoverAndLoad(temporary.Path));

        Assert.Contains("Duplicate mod ID 'same'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DiscoverAndLoad_rejects_missing_dependency()
    {
        using var temporary = new TemporaryDirectory();
        WriteContentMod(temporary.Path, "addon", dependencies: new Dictionary<string, string> { ["absent"] = "*" });

        ModHostException exception = Assert.Throws<ModHostException>(() => ModHost.DiscoverAndLoad(temporary.Path));

        Assert.Contains("missing dependency 'absent'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DiscoverAndLoad_rejects_dependency_cycles()
    {
        using var temporary = new TemporaryDirectory();
        WriteContentMod(temporary.Path, "one", dependencies: new Dictionary<string, string> { ["two"] = "*" });
        WriteContentMod(temporary.Path, "two", dependencies: new Dictionary<string, string> { ["one"] = "*" });

        ModHostException exception = Assert.Throws<ModHostException>(() => ModHost.DiscoverAndLoad(temporary.Path));

        Assert.Contains("cycle", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DiscoverAndLoad_rejects_incompatible_api()
    {
        using var temporary = new TemporaryDirectory();
        WriteContentMod(temporary.Path, "future", apiVersion: ">=3.0.0");

        ModHostException exception = Assert.Throws<ModHostException>(() => ModHost.DiscoverAndLoad(temporary.Path));

        Assert.Contains("requires API", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void V2_mod_receives_one_scoped_capability_context()
    {
        using var temporary = new TemporaryDirectory();
        WriteManifest(temporary.Path, "modern", new
        {
            id = "modern",
            name = "modern",
            version = "1.0.0",
            apiVersion = "^2.0.0",
            capabilities = new[] { "test-loader" }
        });
        var mod = new RecordingV2Mod();

        using ModHost host = ModHost.DiscoverAndLoad(
            temporary.Path,
            new ModHostOptions { Loaders = [new TestLoader(mod)] });
        host.Initialize();
        host.Freeze();

        Assert.NotNull(mod.Context);
        Assert.True(mod.Context!.CapabilitiesV2.TryGet<IModEventBus>(
            ModCapabilityIds.Events,
            out IModEventBus? eventBus));
        Assert.Same(mod.Context.EventBus, eventBus);
        Assert.True(mod.Context.CapabilitiesV2.TryGet<IModClientPlatform>(
            ModCapabilityIds.Client,
            out IModClientPlatform? client));
        Assert.Same(mod.Context.Client, client);
        Assert.Equal(9, mod.Context.CapabilitiesV2.Available.Count);
    }

    [Fact]
    public void Managed_code_requires_explicit_trust_marker()
    {
        using var temporary = new TemporaryDirectory();
        WriteManifest(temporary.Path, "managed", new
        {
            id = "managed",
            name = "managed",
            version = "1.0.0",
            apiVersion = "^1.0.0",
            entryAssembly = "managed.dll",
            entryType = "Example.Entry"
        });

        ModHostException exception = Assert.Throws<ModHostException>(() => ModHost.DiscoverAndLoad(temporary.Path));

        Assert.Contains("trustedCode", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Custom_loader_configures_mod_and_registrations_can_be_frozen()
    {
        using var temporary = new TemporaryDirectory();
        WriteManifest(temporary.Path, "custom", new
        {
            id = "custom",
            name = "custom",
            version = "1.0.0",
            apiVersion = "^1.0.0",
            capabilities = new[] { "test-loader" }
        });
        var mod = new RecordingMod();
        var loader = new TestLoader(mod);

        using ModHost host = ModHost.DiscoverAndLoad(temporary.Path, new ModHostOptions { Loaders = new[] { loader } });
        host.Initialize();
        host.Freeze();

        Assert.True(mod.Configured);
        Assert.True(mod.Started);
        Assert.Single(host.WorldGeneration.Hooks);
        Assert.Throws<InvalidOperationException>(() => mod.Content!.Register(new ResourceId("custom:late"), "late"));
        host.Shutdown();
        Assert.True(mod.Stopped);
    }

    [Theory]
    [InlineData("^1.2.3", "1.9.0", true)]
    [InlineData("^1.2.3", "2.0.0", false)]
    [InlineData(">=1.0.0 <2.0.0", "1.4.2", true)]
    [InlineData("1.2.x", "1.3.0", false)]
    public void Version_constraints_are_semantic_enough(string constraint, string version, bool expected)
    {
        Assert.Equal(expected, VersionConstraint.Parse(constraint).Allows(version));
    }

    private static void WriteContentMod(
        string root,
        string id,
        string? folder = null,
        string apiVersion = "^1.0.0",
        IReadOnlyDictionary<string, string>? dependencies = null)
    {
        string directory = Path.Combine(root, folder ?? id);
        Directory.CreateDirectory(Path.Combine(directory, "content"));
        WriteManifest(root, folder ?? id, new
        {
            id,
            name = id,
            version = "1.0.0",
            apiVersion,
            contentRoot = "content",
            dependencies
        });
    }

    private static void WriteManifest(string root, string folder, object manifest)
    {
        string directory = Path.Combine(root, folder);
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, "tesseris.mod.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
    }

    private sealed class TestLoader : IModLoader
    {
        private readonly IMod mod;
        public TestLoader(IMod mod) => this.mod = mod;
        public string Id => "tests:loader";
        public bool CanLoad(ModDescriptor descriptor) => descriptor.Capabilities.Contains("test-loader", StringComparer.Ordinal);
        public IModLoadResult Load(ModDescriptor descriptor, IModLoadContext context) => new TestResult(mod);
    }

    private sealed class TestResult : IModLoadResult
    {
        public TestResult(IMod instance) => Instance = instance;
        public IMod? Instance { get; }
        public IReadOnlyList<ModContentSource> ContentSources => Array.Empty<ModContentSource>();
        public void Dispose() { }
    }

    private sealed class RecordingMod : IMod
    {
        public bool Configured { get; private set; }
        public bool Started { get; private set; }
        public bool Stopped { get; private set; }
        public IModContent? Content { get; private set; }

        public void Configure(IModContext context)
        {
            Content = context.Content;
            context.Content.Register(new ResourceId("custom:thing"), "value");
            context.WorldGeneration.Register(
                WorldGenerationStage.Features,
                new ResourceId("custom:feature"),
                10,
                new EmptyHook());
            context.Events.OnStarted(() => Started = true);
            context.Events.OnStopping(() => Stopped = true);
            Configured = true;
        }
    }

    private sealed class EmptyHook : IChunkGenerationHook
    {
        public void Generate(IChunkGenerationContext context) { }
    }

    private sealed class RecordingV2Mod : IMod
    {
        public IModContextV2? Context { get; private set; }

        public void Configure(IModContext context) =>
            Context = Assert.IsAssignableFrom<IModContextV2>(context);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Tesseris.ModHostTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
