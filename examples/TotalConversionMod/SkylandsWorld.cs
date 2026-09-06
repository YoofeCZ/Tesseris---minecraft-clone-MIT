using Tesseris.ModApi;

namespace TotalConversionMod;

/// <summary>
/// Complete deterministic replacement terrain. Both entry points use the same surface function, so spawning
/// and chunk generation agree even when workers generate chunks in a different order.
/// </summary>
public sealed class SkylandsGenerator : IModWorldGenerator
{
    private const string Air = "tesseris:air";
    private const string SkyStone = "total_conversion:sky_stone";

    public ModWorldColumn SampleColumn(IWorldColumnContext context) =>
        new(SurfaceAt(context.WorldSeed, context.WorldX, context.WorldZ));

    public void Generate(IChunkGenerationContext chunk)
    {
        int originX = chunk.ChunkX * chunk.SizeX;
        int originY = chunk.ChunkY * chunk.SizeY;
        int originZ = chunk.ChunkZ * chunk.SizeZ;

        for (int localX = 0; localX < chunk.SizeX; localX++)
        for (int localZ = 0; localZ < chunk.SizeZ; localZ++)
        {
            int surface = SurfaceAt(
                chunk.WorldSeed,
                originX + localX,
                originZ + localZ);

            for (int localY = 0; localY < chunk.SizeY; localY++)
            {
                int worldY = originY + localY;
                string block = worldY <= surface && worldY >= surface - 13
                    ? SkyStone
                    : Air;
                chunk.SetBlock(localX, localY, localZ, block);
            }
        }
    }

    private static int SurfaceAt(long seed, int x, int z)
    {
        ulong broad = Hash(seed, FloorDiv(x, 48), FloorDiv(z, 48), 0xA24BAED4963EE407UL);
        ulong detail = Hash(seed, FloorDiv(x, 12), FloorDiv(z, 12), 0x9FB21C651E98DF25UL);
        return 112 + (int)(broad % 72UL) + (int)(detail % 18UL);
    }

    private static int FloorDiv(int value, int divisor)
    {
        int quotient = value / divisor;
        int remainder = value % divisor;
        return remainder < 0 ? quotient - 1 : quotient;
    }

    private static ulong Hash(long seed, int x, int z, ulong salt)
    {
        ulong value = unchecked((ulong)seed) ^ salt;
        value ^= unchecked((ulong)(long)x) * 0x9E3779B185EBCA87UL;
        value ^= unchecked((ulong)(long)z) * 0xC2B2AE3D27D4EB4FUL;
        value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
        value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
        return value ^ (value >> 31);
    }
}

public sealed class SkylandsBiomeSource : IModBiomeSource
{
    public ResourceId Sample(IModBiomeSampleContext context)
    {
        // Add more owned biomes here and select them from context.Hash("biome").
        _ = context.Hash("biome");
        return Ids.Biome;
    }
}
