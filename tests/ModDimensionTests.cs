using Tesseris.Game.Modding;
using Tesseris.Game.World.Definitions;
using Tesseris.ModApi;
using Xunit;

namespace Tesseris.Tests;

public sealed class ModDimensionTests
{
    [Fact]
    public void Storage_keys_are_unique_stable_and_cannot_traverse()
    {
        string root = Path.Combine(Path.GetTempPath(), "tesseris-world-root");
        var overworld = new ResourceId("alpha:world/main");
        var traversal = new ResourceId("alpha:../../outside");
        string first = ModDimensionStorage.Directory(root, overworld);
        string second = ModDimensionStorage.Directory(root, traversal);
        string expectedRoot = Path.GetFullPath(Path.Combine(root, "dimensions")) + Path.DirectorySeparatorChar;

        Assert.StartsWith(expectedRoot, first, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(expectedRoot, second, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(first, second);
        Assert.DoesNotContain("..", ModDimensionStorage.Key(traversal), StringComparison.Ordinal);
        Assert.Equal(ModDimensionStorage.Key(overworld), ModDimensionStorage.Key(overworld));
    }

    [Fact]
    public void Lifecycle_opens_switches_and_closes_without_player_or_window_state()
    {
        ModWorldRuntime runtime = Runtime();
        var lifecycle = new RecordingLifecycle();

        using ModWorldSession session = runtime.Open(lifecycle);
        session.Switch(new ResourceId("test:moon"));
        session.Close();
        session.Close();

        Assert.Equal(ModWorldSessionState.Closed, session.State);
        Assert.Equal(new ResourceId("test:moon"), session.Current.Dimension.Id);
        Assert.Equal(
            ["open:test:overworld", "switching:test:overworld->test:moon", "switched:test:moon", "close:test:moon"],
            lifecycle.Events);
        Assert.Throws<InvalidOperationException>(() => session.Switch(new ResourceId("test:overworld")));
    }

    [Fact]
    public void Dimension_is_present_in_biome_context_and_changes_deterministic_hashes()
    {
        var source = new InspectingSource();
        var generator = new InspectingGenerator();
        ModWorldRuntime runtime = Runtime(generator, source);

        ModWorldRuntime overworld = runtime.SelectDimension(new ResourceId("test:overworld"));
        ModWorldRuntime moon = runtime.SelectDimension(new ResourceId("test:moon"));
        _ = overworld.SampleColumn(13, -9);
        _ = moon.SampleColumn(13, -9);
        _ = overworld.SampleBiome(13, 0, -9);
        _ = moon.SampleBiome(13, 0, -9);

        Assert.Equal([new ResourceId("test:overworld"), new ResourceId("test:moon")], source.Dimensions);
        Assert.Equal(2, generator.Hashes.Distinct().Count());
    }

    private static ModWorldRuntime Runtime(
        IModWorldGenerator? generator = null,
        IModBiomeSource? source = null)
    {
        var registry = new ModWorldDefinitionRegistry();
        IModWorldDefinitionRegistry test = registry.ForMod("test");
        test.RegisterGenerator(new ResourceId("test:generator"), generator ?? new InspectingGenerator());
        test.RegisterBiomeSource(new ResourceId("test:source"), source ?? new InspectingSource());
        test.RegisterBiome(new ModBiomeDefinition(new ResourceId("test:plains"), "Plains", 0.5f, 0.5f, [], []));
        test.RegisterDimension(Dimension("test:overworld"));
        test.RegisterDimension(Dimension("test:moon"));
        test.RegisterPreset(new ModWorldPresetDefinition(
            new ResourceId("test:preset"),
            "Test",
            new ResourceId("test:overworld"),
            [new ResourceId("test:overworld"), new ResourceId("test:moon")],
            []));
        registry.Freeze();
        return ModWorldRuntime.Create(registry, 8123);
    }

    private static ModDimensionDefinition Dimension(string id) => new(
        new ResourceId(id), id, new ResourceId("test:generator"), new ResourceId("test:source"), -32, 64, true, []);

    private sealed class InspectingGenerator : IModWorldGenerator
    {
        public List<ulong> Hashes { get; } = [];

        public ModWorldColumn SampleColumn(IWorldColumnContext context)
        {
            Hashes.Add(context.Hash(context.WorldX, context.WorldZ, "dimension"));
            return new ModWorldColumn(0);
        }

        public void Generate(IChunkGenerationContext context) { }
    }

    private sealed class InspectingSource : IModBiomeSource
    {
        public List<ResourceId> Dimensions { get; } = [];

        public ResourceId Sample(IModBiomeSampleContext context)
        {
            Dimensions.Add(context.DimensionId);
            return new ResourceId("test:plains");
        }
    }

    private sealed class RecordingLifecycle : IModWorldSessionLifecycle
    {
        public List<string> Events { get; } = [];

        public void Opened(ModWorldRuntime runtime) => Events.Add($"open:{runtime.Dimension.Id}");
        public void Switching(ModWorldRuntime from, ModWorldRuntime to) =>
            Events.Add($"switching:{from.Dimension.Id}->{to.Dimension.Id}");
        public void Switched(ModWorldRuntime current) => Events.Add($"switched:{current.Dimension.Id}");
        public void Closed(ModWorldRuntime runtime) => Events.Add($"close:{runtime.Dimension.Id}");
    }
}
