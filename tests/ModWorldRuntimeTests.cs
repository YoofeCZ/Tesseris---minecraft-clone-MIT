using System.Collections.Concurrent;
using Tesseris.Game.Blocks;
using Tesseris.Game.Modding;
using Tesseris.Game.World;
using Tesseris.Game.World.Definitions;
using Tesseris.ModApi;
using Xunit;

namespace Tesseris.Tests;

public sealed class ModWorldRuntimeTests
{
    [Fact]
    public void Preset_uses_default_dimension_and_supports_explicit_selection()
    {
        ModWorldDefinitionRegistry definitions = Definitions(
            dimensions:
            [
                Dimension("test:overworld", "test:generator", "test:source", -32, 64),
                Dimension("test:moon", "test:generator", "test:source", 0, 64),
            ]);

        ModWorldRuntime defaultRuntime = ModWorldRuntime.Create(definitions, 41, new ResourceId("test:preset"));
        ModWorldRuntime moon = ModWorldRuntime.Create(
            definitions,
            41,
            new ResourceId("test:preset"),
            new ResourceId("test:moon"));

        Assert.Equal(new ResourceId("test:overworld"), defaultRuntime.Dimension.Id);
        Assert.Equal(new ResourceId("test:moon"), moon.Dimension.Id);
        Assert.Equal(new ResourceId("test:moon"), defaultRuntime.SelectDimension(new ResourceId("test:moon")).Dimension.Id);
        Assert.Throws<KeyNotFoundException>(() => defaultRuntime.SelectDimension(new ResourceId("test:outside")));
    }

    [Fact]
    public void Sampling_is_deterministic_across_instances_and_concurrent_workers()
    {
        var generator = new HashGenerator(-32, 64);
        ModWorldDefinitionRegistry definitions = Definitions(generator: generator);
        ModWorldRuntime first = ModWorldRuntime.Create(definitions, 918273);
        ModWorldRuntime second = ModWorldRuntime.Create(definitions, 918273);
        var results = new ConcurrentBag<int>();

        Parallel.For(0, 64, _ => results.Add(first.SampleColumn(-913, 441).SurfaceY));

        Assert.Single(results.Distinct());
        Assert.Equal(second.SampleColumn(-913, 441).SurfaceY, results.First());
        Assert.Equal(
            first.SampleBiome(12, 0, -7).Id,
            second.SampleBiome(12, 0, -7).Id);
    }

    [Fact]
    public void Generator_fully_replaces_empty_chunk_and_is_attributed_on_failure()
    {
        BlockRegistry blocks = Blocks();
        ModWorldDefinitionRegistry definitions = Definitions(generator: new FillingGenerator("tesseris:stone"));
        ModWorldRuntime runtime = ModWorldRuntime.Create(definitions, 7);
        var chunk = new Chunk();

        runtime.GenerateChunk(chunk, blocks, 2, 0, -3);

        Assert.Equal(blocks.IndexOf("tesseris:stone"), chunk.GetBlock(0, 0, 0));
        Assert.Equal(blocks.IndexOf("tesseris:stone"), chunk.GetBlock(31, 31, 31));

        ModWorldRuntime broken = ModWorldRuntime.Create(
            Definitions(generator: new ThrowingGenerator()),
            7);
        ModWorldProviderException failure = Assert.Throws<ModWorldProviderException>(
            () => broken.GenerateChunk(new Chunk(), blocks, 0, 0, 0));
        Assert.Equal(new ResourceId("test:generator"), failure.ProviderId);
        Assert.Equal(ModWorldProviderCallback.GenerateChunk, failure.Callback);
        Assert.IsType<SyntheticException>(failure.InnerException);
    }

    [Fact]
    public void Unknown_biome_from_source_is_rejected()
    {
        ModWorldRuntime runtime = ModWorldRuntime.Create(
            Definitions(source: new FixedBiomeSource("missing:biome")),
            12);

        InvalidDataException failure = Assert.Throws<InvalidDataException>(
            () => runtime.SampleBiome(0, 0, 0));

        Assert.Contains("missing:biome", failure.Message, StringComparison.Ordinal);
        Assert.Contains("test:source", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Vertical_bounds_apply_to_columns_biomes_and_chunks()
    {
        ModWorldRuntime invalidColumn = ModWorldRuntime.Create(
            Definitions(generator: new FixedGenerator(32)),
            1);
        ModWorldRuntime runtime = ModWorldRuntime.Create(Definitions(), 1);
        BlockRegistry blocks = Blocks();

        Assert.Throws<InvalidDataException>(() => invalidColumn.SampleColumn(0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => runtime.SampleBiome(0, -33, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => runtime.SampleBiome(0, 32, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            runtime.GenerateChunk(new Chunk(), blocks, 0, -2, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            runtime.GenerateChunk(new Chunk(), blocks, 0, 1, 0));

        runtime.GenerateChunk(new Chunk(), blocks, 0, -1, 0);
        runtime.GenerateChunk(new Chunk(), blocks, 0, 0, 0);
    }

    [Fact]
    public void Fingerprint_changes_with_selected_dimension_and_definitions_but_not_seed()
    {
        ModWorldDefinitionRegistry original = Definitions();
        ModWorldDefinitionRegistry changed = Definitions(temperature: 0.9f);

        ModWorldRuntime seedOne = ModWorldRuntime.Create(original, 1);
        ModWorldRuntime seedTwo = ModWorldRuntime.Create(original, 2);
        ModWorldRuntime modified = ModWorldRuntime.Create(changed, 1);

        Assert.Equal(seedOne.Fingerprint, seedTwo.Fingerprint);
        Assert.NotEqual(seedOne.Fingerprint, modified.Fingerprint);
        Assert.Equal(64, seedOne.Fingerprint.Length);
    }

    [Fact]
    public void Vanilla_factory_preserves_default_surface_biome_and_chunk_output()
    {
        const int seed = 28491;
        BlockRegistry blocks = Blocks();
        ModWorldRuntime runtime = VanillaWorldPresetFactory.CreateRuntime(blocks, seed);
        var vanilla = new TerrainGenerator(blocks, seed);
        vanilla.EnableTrees(blocks);
        var expected = new Chunk();
        var actual = new Chunk();

        vanilla.Generate(expected, new OpenTK.Mathematics.Vector3i(1, 9, -2));
        runtime.GenerateChunk(actual, blocks, 1, 9, -2);

        Assert.Equal(VanillaWorldPresetFactory.PresetId, runtime.Preset.Id);
        Assert.Equal(vanilla.SurfaceHeight(453, -812), runtime.SampleColumn(453, -812).SurfaceY);
        Assert.Equal(
            $"tesseris:{VanillaBiomePath(vanilla.BiomeAt(453, -812))}",
            runtime.SampleBiome(453, 300, -812).Id.Value);
        Assert.Equal(Copy(expected), Copy(actual));
    }

    private static ModWorldDefinitionRegistry Definitions(
        IModWorldGenerator? generator = null,
        IModBiomeSource? source = null,
        IReadOnlyList<ModDimensionDefinition>? dimensions = null,
        float temperature = 0.5f)
    {
        var registry = new ModWorldDefinitionRegistry();
        IModWorldDefinitionRegistry test = registry.ForMod("test");
        test.RegisterGenerator(new ResourceId("test:generator"), generator ?? new FixedGenerator(0));
        test.RegisterBiomeSource(new ResourceId("test:source"), source ?? new FixedBiomeSource("test:plains"));
        test.RegisterBiome(new ModBiomeDefinition(
            new ResourceId("test:plains"), "Plains", temperature, 0.5f, [], []));
        IReadOnlyList<ModDimensionDefinition> configuredDimensions = dimensions ??
            [Dimension("test:overworld", "test:generator", "test:source", -32, 64)];
        foreach (ModDimensionDefinition dimension in configuredDimensions) test.RegisterDimension(dimension);
        test.RegisterPreset(new ModWorldPresetDefinition(
            new ResourceId("test:preset"),
            "Test",
            configuredDimensions[0].Id,
            configuredDimensions.Select(value => value.Id).ToArray(),
            []));
        registry.Freeze();
        return registry;
    }

    private static ModDimensionDefinition Dimension(
        string id,
        string generator,
        string source,
        int minimumY,
        int height) =>
        new(new ResourceId(id), id, new ResourceId(generator), new ResourceId(source), minimumY, height, true, []);

    private static BlockRegistry Blocks() =>
        BlockRegistry.LoadFromDirectory(Path.Combine(AppContext.BaseDirectory, "assets", "blocks"));

    private static ushort[] Copy(Chunk chunk)
    {
        var result = new ushort[Chunk.Volume];
        chunk.CopyTo(result);
        return result;
    }

    private static string VanillaBiomePath(Biome biome) => biome switch
    {
        Biome.Plains => "plains",
        Biome.Savanna => "savanna",
        Biome.Desert => "desert",
        Biome.Badlands => "badlands",
        Biome.Tundra => "tundra",
        Biome.Highlands => "highlands",
        Biome.SnowyPeaks => "snowy_peaks",
        Biome.StonyPeaks => "stony_peaks",
        Biome.FrozenPeaks => "frozen_peaks",
        _ => throw new ArgumentOutOfRangeException(nameof(biome)),
    };

    private sealed class FixedGenerator(int surface) : IModWorldGenerator
    {
        public ModWorldColumn SampleColumn(IWorldColumnContext context) => new(surface);
        public void Generate(IChunkGenerationContext context) { }
    }

    private sealed class HashGenerator(int minimumY, int height) : IModWorldGenerator
    {
        public ModWorldColumn SampleColumn(IWorldColumnContext context) =>
            new(minimumY + (int)(context.Hash(context.WorldX, context.WorldZ, "surface") % (uint)height));
        public void Generate(IChunkGenerationContext context) { }
    }

    private sealed class FillingGenerator(string blockId) : IModWorldGenerator
    {
        public ModWorldColumn SampleColumn(IWorldColumnContext context) => new(0);

        public void Generate(IChunkGenerationContext context)
        {
            for (int y = 0; y < context.SizeY; y++)
            for (int z = 0; z < context.SizeZ; z++)
            for (int x = 0; x < context.SizeX; x++)
            {
                context.SetBlock(x, y, z, blockId);
            }
        }
    }

    private sealed class ThrowingGenerator : IModWorldGenerator
    {
        public ModWorldColumn SampleColumn(IWorldColumnContext context) => throw new SyntheticException();
        public void Generate(IChunkGenerationContext context) => throw new SyntheticException();
    }

    private sealed class FixedBiomeSource(string id) : IModBiomeSource
    {
        public ResourceId Sample(IModBiomeSampleContext context) => new(id);
    }

    private sealed class SyntheticException : Exception;
}
