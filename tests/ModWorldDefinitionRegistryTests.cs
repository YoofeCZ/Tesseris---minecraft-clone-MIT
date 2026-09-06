using Tesseris.Game.Modding;
using Tesseris.ModApi;
using Xunit;

namespace Tesseris.Tests;

public sealed class ModWorldDefinitionRegistryTests
{
    [Fact]
    public void Definitions_are_owner_scoped_frozen_and_sorted()
    {
        var registry = new ModWorldDefinitionRegistry();
        IModWorldDefinitionRegistry ruby = registry.ForMod("ruby");

        ruby.RegisterGenerator(new ResourceId("ruby:generator"), new Generator());
        ruby.RegisterBiomeSource(new ResourceId("ruby:biomes"), new BiomeSource());
        ruby.RegisterBiome(Biome("ruby:z"));
        ruby.RegisterBiome(Biome("ruby:a"));
        ruby.RegisterDimension(Dimension());
        ruby.RegisterPreset(Preset());

        registry.Freeze();

        Assert.True(registry.IsFrozen);
        Assert.Equal(["ruby:a", "ruby:z"], registry.Biomes.Select(value => value.Id.Value));
        Assert.True(registry.TryGetGenerator(new ResourceId("ruby:generator"), out IModWorldGenerator? generator));
        Assert.IsType<Generator>(generator);
        Assert.Throws<InvalidOperationException>(() => ruby.RegisterBiome(Biome("ruby:late")));
    }

    [Fact]
    public void Registration_takes_defensive_snapshots()
    {
        var registry = new ModWorldDefinitionRegistry();
        IModWorldDefinitionRegistry ruby = registry.ForMod("ruby");
        var dimensions = new List<ResourceId> { new("ruby:overworld") };
        var payload = new byte[] { 4, 5 };

        ruby.RegisterPreset(new ModWorldPresetDefinition(
            new ResourceId("ruby:preset"), "Ruby", new ResourceId("ruby:overworld"), dimensions,
            [new ModComponentValue(
                new ResourceId("ruby:value"),
                new ModSerializedValue(new ResourceId("ruby:bytes"), 1, payload))]));

        dimensions.Add(new ResourceId("ruby:late"));
        payload[0] = 99;

        Assert.True(registry.TryGetPreset(new ResourceId("ruby:preset"), out ModWorldPresetDefinition? preset));
        Assert.Single(preset!.DimensionIds);
        Assert.Equal(4, preset.Properties[0].Value.Payload.Span[0]);
    }

    [Fact]
    public void Freeze_rejects_unresolved_dimension_dependencies()
    {
        var registry = new ModWorldDefinitionRegistry();
        IModWorldDefinitionRegistry ruby = registry.ForMod("ruby");
        ruby.RegisterDimension(Dimension());
        ruby.RegisterPreset(Preset());

        ModHostException failure = Assert.Throws<ModHostException>(() => registry.Freeze());

        Assert.Contains("missing generator", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Foreign_namespace_and_duplicate_ids_are_rejected()
    {
        var registry = new ModWorldDefinitionRegistry();
        IModWorldDefinitionRegistry ruby = registry.ForMod("ruby");
        IModWorldDefinitionRegistry copper = registry.ForMod("copper");

        Assert.Throws<ModHostException>(() => ruby.RegisterBiome(Biome("copper:ore")));
        ruby.RegisterBiome(Biome("ruby:one"));
        Assert.Throws<ModHostException>(() => copper.RegisterBiome(Biome("ruby:one")));
    }

    [Fact]
    public void Registration_is_game_thread_only()
    {
        var registry = new ModWorldDefinitionRegistry();
        IModWorldDefinitionRegistry ruby = registry.ForMod("ruby");

        Assert.IsType<InvalidOperationException>(RunOnDedicatedThread(
            () => ruby.RegisterBiome(Biome("ruby:off_thread"))));
    }

    private static Exception? RunOnDedicatedThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.Start();
        thread.Join();
        return failure;
    }

    private static ModWorldPresetDefinition Preset() => new(
        new ResourceId("ruby:preset"),
        "Ruby world",
        new ResourceId("ruby:overworld"),
        [new ResourceId("ruby:overworld")],
        []);

    private static ModDimensionDefinition Dimension() => new(
        new ResourceId("ruby:overworld"),
        "Ruby overworld",
        new ResourceId("ruby:generator"),
        new ResourceId("ruby:biomes"),
        -64,
        384,
        true,
        []);

    private static ModBiomeDefinition Biome(string id) => new(
        new ResourceId(id), "Biome", 0.5f, 0.5f, [], []);

    private sealed class Generator : IModWorldGenerator
    {
        public ModWorldColumn SampleColumn(IWorldColumnContext context) => new(0);

        public void Generate(IChunkGenerationContext context) { }
    }

    private sealed class BiomeSource : IModBiomeSource
    {
        public ResourceId Sample(IModBiomeSampleContext context) => new("ruby:a");
    }
}
