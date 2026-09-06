using OpenTK.Mathematics;
using Tesseris.Game.Blocks;
using Tesseris.Game.Modding;
using Tesseris.Game.World;
using Tesseris.ModApi;
using Xunit;

namespace Tesseris.Tests;

public sealed class ModWorldGenerationPipelineTests
{
    private const int Seed = 731_991;

    private static BlockRegistry Registry() =>
        BlockRegistry.LoadFromDirectory(Path.Combine(AppContext.BaseDirectory, "assets", "blocks"));

    [Fact]
    public void Hooks_run_in_frozen_stage_priority_and_id_order()
    {
        BlockRegistry blocks = Registry();
        var calls = new List<string>();
        RegisteredWorldGenerationHook[] registrations =
        [
            Registration("test:z", WorldGenerationStage.Features, 0, new RecordingHook("features", calls)),
            Registration("test:b", WorldGenerationStage.Surface, 10, new RecordingHook("b", calls)),
            Registration("test:a", WorldGenerationStage.Surface, 10, new RecordingHook("a", calls)),
            Registration("test:first", WorldGenerationStage.Surface, -10, new RecordingHook("priority", calls)),
            Registration("test:base", WorldGenerationStage.BaseTerrain, 100, new RecordingHook("base", calls)),
        ];

        var pipeline = new ModWorldGenerationPipeline(blocks, registrations);

        // Changing the source array after construction must not change the frozen execution list.
        registrations[0] = Registration(
            "test:replacement",
            WorldGenerationStage.PostProcess,
            0,
            new RecordingHook("replacement", calls));

        pipeline.Apply(new Chunk(), new Vector3i(2, 3, 4), Seed);

        Assert.Equal(["base", "priority", "a", "b", "features"], calls);
    }

    [Fact]
    public void Terrain_generator_runs_hooks_after_an_early_homogeneous_return()
    {
        BlockRegistry blocks = Registry();
        var hook = new InspectingHook(context =>
        {
            Assert.Equal(0, context.ChunkX);
            Assert.Equal(TerrainGenerator.WorldHeightChunks, context.ChunkY);
            Assert.Equal(-2, context.ChunkZ);
            Assert.Equal(Chunk.Size, context.SizeX);
            Assert.Equal(Chunk.Size, context.SizeY);
            Assert.Equal(Chunk.Size, context.SizeZ);
            Assert.Equal(Seed, context.WorldSeed);
            Assert.Equal("tesseris:air", context.GetBlockId(7, 8, 9));
            context.SetBlock(7, 8, 9, "tesseris:stone");
        });
        var generator = new TerrainGenerator(
            blocks,
            Seed,
            [Registration("test:air_feature", WorldGenerationStage.Features, 0, hook)]);
        var chunk = new Chunk();

        generator.Generate(chunk, new Vector3i(0, TerrainGenerator.WorldHeightChunks, -2));

        Assert.Equal(blocks.IndexOf("tesseris:stone"), chunk.GetBlock(7, 8, 9));
        Assert.Equal(1, hook.CallCount);
    }

    [Fact]
    public void Deterministic_facilities_match_across_instances_and_concurrent_workers()
    {
        BlockRegistry blocks = Registry();
        var hook = new RandomPaintingHook();
        RegisteredWorldGenerationHook registration = Registration(
            "test:random",
            WorldGenerationStage.Decoration,
            0,
            hook);
        var first = new TerrainGenerator(blocks, Seed, [registration]);
        var second = new TerrainGenerator(blocks, Seed, [registration]);
        var chunks = Enumerable.Range(0, 12).Select(_ => new Chunk()).ToArray();
        var position = new Vector3i(-17, TerrainGenerator.WorldHeightChunks, 29);

        Parallel.ForEach(chunks, chunk => first.Generate(chunk, position));

        var expected = new Chunk();
        second.Generate(expected, position);
        ushort[] expectedBlocks = Copy(expected);

        foreach (Chunk chunk in chunks)
        {
            Assert.Equal(expectedBlocks, Copy(chunk));
        }
    }

    [Fact]
    public void Zero_hook_pipeline_preserves_vanilla_output()
    {
        BlockRegistry blocks = Registry();
        var vanilla = new TerrainGenerator(blocks, Seed);
        var withEmptyPipeline = new TerrainGenerator(
            blocks,
            Seed,
            new ModWorldGenerationPipeline(blocks, Array.Empty<RegisteredWorldGenerationHook>()));
        var position = new Vector3i(-3, 8, 11);
        var expected = new Chunk();
        var actual = new Chunk();

        vanilla.Generate(expected, position);
        withEmptyPipeline.Generate(actual, position);

        Assert.Equal(expected.IsHomogeneous, actual.IsHomogeneous);
        Assert.Equal(Copy(expected), Copy(actual));
    }

    [Fact]
    public void Base_generator_replaces_vanilla_and_supplies_the_canonical_surface()
    {
        BlockRegistry blocks = Registry();
        var worldGenerator = new FlatReplacementGenerator(surfaceY: 47, "tesseris:stone");
        var inspectingHook = new InspectingHook(context =>
        {
            Assert.Equal("tesseris:stone", context.GetBlockId(3, 4, 5));
            context.SetBlock(3, 4, 5, "tesseris:dirt");
        });
        var pipeline = new ModWorldGenerationPipeline(
            blocks,
            [Registration("addon:after_base", WorldGenerationStage.Decoration, 0, inspectingHook, "addon")],
            new RegisteredWorldGenerator(
                "total",
                new ResourceId("total:flat_world"),
                worldGenerator));
        var terrain = new TerrainGenerator(blocks, Seed, pipeline);
        var chunk = new Chunk();

        terrain.Generate(chunk, Vector3i.Zero);

        Assert.Equal(47, terrain.SurfaceHeight(1234, -987));
        Assert.Equal(blocks.IndexOf("tesseris:stone"), chunk.GetBlock(0, 0, 0));
        Assert.Equal(blocks.IndexOf("tesseris:stone"), chunk.GetBlock(31, 31, 31));
        Assert.Equal(blocks.IndexOf("tesseris:dirt"), chunk.GetBlock(3, 4, 5));
        Assert.Equal(1, worldGenerator.GenerateCalls);
        Assert.Equal(1, inspectingHook.CallCount);
    }

    [Fact]
    public void Base_generator_registration_is_owned_and_exclusive()
    {
        var registry = new ModWorldGenerationRegistry();
        IWorldGenerationRegistry first = registry.ForMod("first");
        IWorldGenerationRegistry second = registry.ForMod("second");

        first.SetBaseGenerator(
            new ResourceId("first:world"),
            new FlatReplacementGenerator(10, "tesseris:stone"));

        ModHostException exception = Assert.Throws<ModHostException>(() =>
            second.SetBaseGenerator(
                new ResourceId("second:world"),
                new FlatReplacementGenerator(20, "tesseris:dirt")));

        Assert.Contains("first:world", exception.Message, StringComparison.Ordinal);
        Assert.Contains("second:world", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(-1, 0, 0)]
    [InlineData(32, 0, 0)]
    [InlineData(0, -1, 0)]
    [InlineData(0, 0, 32)]
    public void Invalid_local_coordinates_are_attributed_to_the_hook(int x, int y, int z)
    {
        BlockRegistry blocks = Registry();
        var pipeline = new ModWorldGenerationPipeline(
            blocks,
            [Registration(
                "broken:outside",
                WorldGenerationStage.Carving,
                4,
                new InspectingHook(context => context.SetBlock(x, y, z, "tesseris:stone")),
                "broken")]);

        ModWorldGenerationException exception = Assert.Throws<ModWorldGenerationException>(
            () => pipeline.Apply(new Chunk(), new Vector3i(1, 2, 3), Seed));

        Assert.Equal("broken", exception.ModId);
        Assert.Equal(new ResourceId("broken:outside"), exception.HookId);
        Assert.Equal(WorldGenerationStage.Carving, exception.Stage);
        Assert.Equal(new Vector3i(1, 2, 3), exception.ChunkPosition);
        Assert.IsType<ArgumentOutOfRangeException>(exception.InnerException);
    }

    [Fact]
    public void Unknown_block_id_is_attributed_to_the_hook()
    {
        BlockRegistry blocks = Registry();
        var pipeline = new ModWorldGenerationPipeline(
            blocks,
            [Registration(
                "broken:unknown_block",
                WorldGenerationStage.Surface,
                0,
                new InspectingHook(context => context.SetBlock(0, 0, 0, "broken:missing")),
                "broken")]);

        ModWorldGenerationException exception = Assert.Throws<ModWorldGenerationException>(
            () => pipeline.Apply(new Chunk(), Vector3i.Zero, Seed));

        Assert.Contains("broken", exception.Message, StringComparison.Ordinal);
        Assert.Contains("broken:unknown_block", exception.Message, StringComparison.Ordinal);
        KeyNotFoundException inner = Assert.IsType<KeyNotFoundException>(exception.InnerException);
        Assert.Contains("broken:missing", inner.Message, StringComparison.Ordinal);
    }

    private static RegisteredWorldGenerationHook Registration(
        string id,
        WorldGenerationStage stage,
        int priority,
        IChunkGenerationHook hook,
        string modId = "test") =>
        new(modId, stage, new ResourceId(id), priority, hook);

    private static ushort[] Copy(Chunk chunk)
    {
        var blocks = new ushort[Chunk.Volume];
        chunk.CopyTo(blocks);
        return blocks;
    }

    private sealed class RecordingHook : IChunkGenerationHook
    {
        private readonly string _name;
        private readonly List<string> _calls;

        public RecordingHook(string name, List<string> calls)
        {
            _name = name;
            _calls = calls;
        }

        public void Generate(IChunkGenerationContext context)
        {
            _calls.Add(_name);
            _ = context;
        }
    }

    private sealed class InspectingHook : IChunkGenerationHook
    {
        private readonly Action<IChunkGenerationContext> _generate;
        private int _callCount;

        public InspectingHook(Action<IChunkGenerationContext> generate) => _generate = generate;

        public int CallCount => Volatile.Read(ref _callCount);

        public void Generate(IChunkGenerationContext context)
        {
            Interlocked.Increment(ref _callCount);
            _generate(context);
        }
    }

    private sealed class RandomPaintingHook : IChunkGenerationHook
    {
        public void Generate(IChunkGenerationContext context)
        {
            IDeterministicRandom forkBeforeConsumption = context.Random.Fork("fork");

            for (int i = 0; i < 128; i++)
            {
                int x = context.Random.NextInt(context.SizeX);
                int y = context.Random.NextInt(context.SizeY);
                int z = context.Random.NextInt(context.SizeZ);
                string block = (context.Hash(x, y, z, "material") & 1) == 0
                    ? "tesseris:stone"
                    : "tesseris:dirt";
                context.SetBlock(x, y, z, block);
            }

            // A fork belongs to the hook stream, not to the current consumption position.
            IDeterministicRandom forkAfterConsumption = context.Random.Fork("fork");
            Assert.Equal(forkBeforeConsumption.NextUInt64(), forkAfterConsumption.NextUInt64());
            Assert.InRange(context.Random.NextDouble(), 0.0, 0.9999999999999999);
        }
    }

    private sealed class FlatReplacementGenerator : IModWorldGenerator
    {
        private readonly int _surfaceY;
        private readonly string _blockId;
        private int _generateCalls;

        public FlatReplacementGenerator(int surfaceY, string blockId)
        {
            _surfaceY = surfaceY;
            _blockId = blockId;
        }

        public int GenerateCalls => Volatile.Read(ref _generateCalls);

        public ModWorldColumn SampleColumn(IWorldColumnContext context)
        {
            Assert.Equal(Seed, context.WorldSeed);
            _ = context.Hash(context.WorldX, context.WorldZ, "surface");
            return new ModWorldColumn(_surfaceY);
        }

        public void Generate(IChunkGenerationContext context)
        {
            Interlocked.Increment(ref _generateCalls);
            for (int x = 0; x < context.SizeX; x++)
            for (int y = 0; y < context.SizeY; y++)
            for (int z = 0; z < context.SizeZ; z++)
            {
                int worldY = (context.ChunkY * context.SizeY) + y;
                if (worldY <= _surfaceY)
                {
                    context.SetBlock(x, y, z, _blockId);
                }
            }
        }
    }
}
