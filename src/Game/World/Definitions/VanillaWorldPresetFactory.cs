using System.Collections.Concurrent;
using OpenTK.Mathematics;
using Tesseris.Game.Blocks;
using Tesseris.Game.Modding;
using Tesseris.ModApi;

namespace Tesseris.Game.World.Definitions;

/// <summary>Registers the existing Tesseris terrain as an ordinary default preset.</summary>
public static class VanillaWorldPresetFactory
{
    public static readonly ResourceId PresetId = new("tesseris:default");
    public static readonly ResourceId OverworldId = new("tesseris:overworld");
    public static readonly ResourceId GeneratorId = new("tesseris:terrain");
    public static readonly ResourceId BiomeSourceId = new("tesseris:climate");

    public static void RegisterDefaults(ModWorldDefinitionRegistry definitions, BlockRegistry blocks)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(blocks);

        IModWorldDefinitionRegistry vanilla = definitions.ForMod("tesseris");
        var generator = new VanillaGenerator(blocks);
        vanilla.RegisterGenerator(GeneratorId, generator);
        vanilla.RegisterBiomeSource(BiomeSourceId, new VanillaBiomeSource(generator));

        foreach ((Biome biome, string path, string displayName, float temperature, float humidity) in Biomes)
        {
            _ = biome;
            vanilla.RegisterBiome(new ModBiomeDefinition(
                new ResourceId($"tesseris:{path}"),
                displayName,
                temperature,
                humidity,
                [],
                []));
        }

        vanilla.RegisterDimension(new ModDimensionDefinition(
            OverworldId,
            "Overworld",
            GeneratorId,
            BiomeSourceId,
            0,
            TerrainGenerator.WorldHeight,
            true,
            []));
        vanilla.RegisterPreset(new ModWorldPresetDefinition(
            PresetId,
            "Default",
            OverworldId,
            [OverworldId],
            []));
    }

    public static ModWorldRuntime CreateRuntime(BlockRegistry blocks, long worldSeed)
    {
        var definitions = new ModWorldDefinitionRegistry();
        RegisterDefaults(definitions, blocks);
        definitions.Freeze();
        return ModWorldRuntime.Create(definitions, worldSeed, PresetId);
    }

    private static readonly (Biome Biome, string Path, string DisplayName, float Temperature, float Humidity)[] Biomes =
    [
        (Biome.Plains, "plains", "Plains", 0.6f, 0.6f),
        (Biome.Savanna, "savanna", "Savanna", 0.9f, 0.25f),
        (Biome.Desert, "desert", "Desert", 1.0f, 0.05f),
        (Biome.Badlands, "badlands", "Badlands", 0.95f, 0.0f),
        (Biome.Tundra, "tundra", "Tundra", 0.15f, 0.4f),
        (Biome.Highlands, "highlands", "Highlands", 0.35f, 0.55f),
        (Biome.SnowyPeaks, "snowy_peaks", "Snowy Peaks", 0.05f, 0.5f),
        (Biome.StonyPeaks, "stony_peaks", "Stony Peaks", 0.4f, 0.35f),
        (Biome.FrozenPeaks, "frozen_peaks", "Frozen Peaks", -0.2f, 0.45f),
    ];

    private sealed class VanillaGenerator : IModWorldGenerator
    {
        private readonly BlockRegistry blocks;
        private readonly ConcurrentDictionary<long, TerrainGenerator> generators = new();

        public VanillaGenerator(BlockRegistry blocks) => this.blocks = blocks;

        public ModWorldColumn SampleColumn(IWorldColumnContext context) =>
            new(Get(context.WorldSeed).SurfaceHeight(context.WorldX, context.WorldZ));

        public void Generate(IChunkGenerationContext context)
        {
            TerrainGenerator terrain = Get(context.WorldSeed);
            var position = new Vector3i(context.ChunkX, context.ChunkY, context.ChunkZ);
            if (context is IChunkBackedGenerationContext { Chunk: { } destination })
            {
                terrain.Generate(destination, position);
                return;
            }

            var generated = new Chunk();
            terrain.Generate(generated, position);
            for (int y = 0; y < context.SizeY; y++)
            for (int z = 0; z < context.SizeZ; z++)
            for (int x = 0; x < context.SizeX; x++)
            {
                ushort block = generated.GetBlock(x, y, z);
                context.SetBlock(x, y, z, blocks.Definition(block).Id);
            }
        }

        public Biome BiomeAt(long worldSeed, int worldX, int worldZ) =>
            Get(worldSeed).BiomeAt(worldX, worldZ);

        private TerrainGenerator Get(long worldSeed) => generators.GetOrAdd(worldSeed, static (seed, registry) =>
        {
            var terrain = new TerrainGenerator(registry, unchecked((int)seed));
            terrain.EnableTrees(registry);
            return terrain;
        }, blocks);
    }

    private sealed class VanillaBiomeSource(VanillaGenerator generator) : IModBiomeSource
    {
        public ResourceId Sample(IModBiomeSampleContext context)
        {
            Biome biome = generator.BiomeAt(context.WorldSeed, context.WorldX, context.WorldZ);
            foreach ((Biome candidate, string path, _, _, _) in Biomes)
            {
                if (candidate == biome)
                {
                    return new ResourceId($"tesseris:{path}");
                }
            }

            throw new InvalidOperationException($"Vanilla biome '{biome}' has no stable resource ID.");
        }
    }
}
