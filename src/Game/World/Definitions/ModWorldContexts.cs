using System.Text;
using Tesseris.ModApi;

namespace Tesseris.Game.World.Definitions;

internal interface IDimensionWorldColumnContext : IWorldColumnContext
{
    ResourceId DimensionId { get; }
}

internal interface IDimensionChunkGenerationContext : IChunkGenerationContext
{
    ResourceId DimensionId { get; }
}

internal interface IChunkBackedGenerationContext
{
    Chunk? Chunk { get; }
}

internal static class ModWorldStableHash
{
    private const ulong Offset = 0xCBF29CE484222325UL;
    private const ulong Prime = 0x100000001B3UL;

    public static ulong Seed(long worldSeed, ResourceId dimensionId, ResourceId providerId, string purpose)
    {
        ulong hash = AddUInt64(Offset, unchecked((ulong)worldSeed));
        hash = AddText(hash, dimensionId.Value);
        hash = AddText(hash, providerId.Value);
        hash = AddText(hash, purpose);
        return Mix(hash);
    }

    public static ulong Coordinates(ulong seed, int x, int y, int z, string salt)
    {
        ArgumentNullException.ThrowIfNull(salt);
        ulong hash = AddUInt64(Offset, seed);
        hash = AddUInt64(hash, unchecked((ulong)(long)x));
        hash = AddUInt64(hash, unchecked((ulong)(long)y));
        hash = AddUInt64(hash, unchecked((ulong)(long)z));
        hash = AddText(hash, salt);
        return Mix(hash);
    }

    public static ulong Text(ulong seed, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Mix(AddText(AddUInt64(Offset, seed), value));
    }

    private static ulong Mix(ulong value)
    {
        value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
        value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
        return value ^ (value >> 31);
    }

    private static ulong AddUInt64(ulong hash, ulong value)
    {
        for (int shift = 0; shift < 64; shift += 8)
        {
            hash = (hash ^ (byte)(value >> shift)) * Prime;
        }

        return hash;
    }

    private static ulong AddText(ulong hash, string value)
    {
        int byteCount = Encoding.UTF8.GetByteCount(value);
        hash = AddUInt64(hash, (ulong)byteCount);
        Span<byte> bytes = byteCount <= 256 ? stackalloc byte[byteCount] : new byte[byteCount];
        Encoding.UTF8.GetBytes(value, bytes);
        foreach (byte item in bytes)
        {
            hash = (hash ^ item) * Prime;
        }

        return hash;
    }
}

internal sealed class ModWorldDeterministicRandom : IDeterministicRandom
{
    private const ulong Increment = 0x9E3779B97F4A7C15UL;
    private readonly ulong streamSeed;
    private ulong state;

    public ModWorldDeterministicRandom(ulong streamSeed)
    {
        this.streamSeed = streamSeed;
        state = streamSeed;
    }

    public ulong NextUInt64()
    {
        state += Increment;
        ulong value = state;
        value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
        value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
        return value ^ (value >> 31);
    }

    public int NextInt(int exclusiveMax)
    {
        if (exclusiveMax <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(exclusiveMax));
        }

        return (int)NextBounded((ulong)exclusiveMax);
    }

    public int NextInt(int inclusiveMin, int exclusiveMax)
    {
        if (inclusiveMin >= exclusiveMax)
        {
            throw new ArgumentOutOfRangeException(nameof(exclusiveMax));
        }

        ulong range = (ulong)((long)exclusiveMax - inclusiveMin);
        return (int)(inclusiveMin + (long)NextBounded(range));
    }

    public double NextDouble() => (NextUInt64() >> 11) * (1.0 / (1UL << 53));

    public IDeterministicRandom Fork(string salt) =>
        new ModWorldDeterministicRandom(ModWorldStableHash.Text(streamSeed, salt));

    private ulong NextBounded(ulong exclusiveMax)
    {
        ulong threshold = unchecked(0UL - exclusiveMax) % exclusiveMax;
        while (true)
        {
            ulong value = NextUInt64();
            if (value >= threshold)
            {
                return value % exclusiveMax;
            }
        }
    }
}

internal sealed class RuntimeColumnContext : IDimensionWorldColumnContext
{
    private readonly ulong seed;

    public RuntimeColumnContext(
        long worldSeed,
        ResourceId dimensionId,
        ResourceId generatorId,
        int worldX,
        int worldZ)
    {
        WorldSeed = worldSeed;
        DimensionId = dimensionId;
        WorldX = worldX;
        WorldZ = worldZ;
        seed = ModWorldStableHash.Seed(worldSeed, dimensionId, generatorId, "column");
    }

    public long WorldSeed { get; }
    public ResourceId DimensionId { get; }
    public int WorldX { get; }
    public int WorldZ { get; }

    public ulong Hash(int x, int z, string salt = "") =>
        ModWorldStableHash.Coordinates(seed, x, 0, z, salt);
}

internal sealed class RuntimeBiomeContext : IModBiomeSampleContext
{
    private readonly ulong seed;

    public RuntimeBiomeContext(
        long worldSeed,
        ResourceId dimensionId,
        ResourceId biomeSourceId,
        int worldX,
        int worldY,
        int worldZ)
    {
        WorldSeed = worldSeed;
        DimensionId = dimensionId;
        WorldX = worldX;
        WorldY = worldY;
        WorldZ = worldZ;
        seed = ModWorldStableHash.Seed(worldSeed, dimensionId, biomeSourceId, "biome");
    }

    public long WorldSeed { get; }
    public ResourceId DimensionId { get; }
    public int WorldX { get; }
    public int WorldY { get; }
    public int WorldZ { get; }

    public ulong Hash(string salt = "") =>
        ModWorldStableHash.Coordinates(seed, WorldX, WorldY, WorldZ, salt);
}

internal sealed class RuntimeChunkContext : IDimensionChunkGenerationContext, IChunkBackedGenerationContext
{
    private readonly IChunkGenerationContext destination;
    private readonly ulong seed;

    public RuntimeChunkContext(
        IChunkGenerationContext destination,
        long worldSeed,
        ResourceId dimensionId,
        ResourceId generatorId)
    {
        this.destination = destination;
        WorldSeed = worldSeed;
        DimensionId = dimensionId;
        seed = ModWorldStableHash.Seed(worldSeed, dimensionId, generatorId, "chunk");
        Random = new ModWorldDeterministicRandom(Hash(destination.ChunkX, destination.ChunkY, destination.ChunkZ, "random"));
    }

    public long WorldSeed { get; }
    public ResourceId DimensionId { get; }
    public int ChunkX => destination.ChunkX;
    public int ChunkY => destination.ChunkY;
    public int ChunkZ => destination.ChunkZ;
    public int SizeX => destination.SizeX;
    public int SizeY => destination.SizeY;
    public int SizeZ => destination.SizeZ;
    public IDeterministicRandom Random { get; }
    public Chunk? Chunk => (destination as IChunkBackedGenerationContext)?.Chunk;

    public string GetBlockId(int localX, int localY, int localZ)
    {
        ValidateLocal(localX, localY, localZ);
        return destination.GetBlockId(localX, localY, localZ);
    }

    public void SetBlock(int localX, int localY, int localZ, string stableBlockId)
    {
        ValidateLocal(localX, localY, localZ);
        destination.SetBlock(localX, localY, localZ, stableBlockId);
    }

    public ulong Hash(int x, int y, int z, string salt = "") =>
        ModWorldStableHash.Coordinates(seed, x, y, z, salt);

    private void ValidateLocal(int x, int y, int z)
    {
        if ((uint)x >= (uint)SizeX) throw new ArgumentOutOfRangeException(nameof(x));
        if ((uint)y >= (uint)SizeY) throw new ArgumentOutOfRangeException(nameof(y));
        if ((uint)z >= (uint)SizeZ) throw new ArgumentOutOfRangeException(nameof(z));
    }
}
