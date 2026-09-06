namespace Tesseris.ModApi;

public enum WorldGenerationStage
{
    BaseTerrain = 0,
    Carving = 100,
    Surface = 200,
    Features = 300,
    Decoration = 400,
    PostProcess = 500
}

public interface IWorldGenerationRegistry
{
    /// <summary>
    /// Replaces vanilla terrain generation completely. Only one replacement generator may be active
    /// in a modpack. It receives empty chunks and supplies the canonical surface sampler used by
    /// spawning and chunk streaming. Modifier hooks registered with <see cref="Register"/> run after it.
    /// </summary>
    void SetBaseGenerator(ResourceId id, IModWorldGenerator generator)
        => throw new NotSupportedException("This mod host does not expose replaceable world generation.");

    /// <summary>
    /// Registers a chunk hook. Lower priorities run first; equal priorities are ordered by stable resource ID.
    /// A hook can be invoked concurrently for different chunks and must therefore be thread-safe.
    /// </summary>
    void Register(WorldGenerationStage stage, ResourceId id, int priority, IChunkGenerationHook hook);
}

/// <summary>
/// A complete deterministic world generator. Implementations may be invoked concurrently and must
/// derive all output only from the supplied contexts. They must not read neighbouring loaded chunks.
/// </summary>
public interface IModWorldGenerator
{
    /// <summary>Returns the canonical top solid block for one world column.</summary>
    ModWorldColumn SampleColumn(IWorldColumnContext context);

    /// <summary>Fills one initially empty chunk.</summary>
    void Generate(IChunkGenerationContext context);
}

/// <summary>Column information required by the engine before a chunk exists.</summary>
public readonly record struct ModWorldColumn(int SurfaceY);

/// <summary>A deterministic, thread-safe sampling context for one horizontal world coordinate.</summary>
public interface IWorldColumnContext
{
    long WorldSeed { get; }

    int WorldX { get; }

    int WorldZ { get; }

    /// <summary>Returns a stable value independent of worker order and process runtime.</summary>
    ulong Hash(int x, int z, string salt = "");
}

public interface IChunkGenerationHook
{
    /// <summary>
    /// Generates or changes one chunk. Implementations must only use the supplied deterministic random/hash
    /// facilities and must be safe to call concurrently for separate chunks.
    /// </summary>
    void Generate(IChunkGenerationContext context);
}

public interface IChunkGenerationContext
{
    long WorldSeed { get; }

    int ChunkX { get; }

    int ChunkY { get; }

    int ChunkZ { get; }

    int SizeX { get; }

    int SizeY { get; }

    int SizeZ { get; }

    /// <summary>A hook-specific deterministic stream. It is never shared with another chunk invocation.</summary>
    IDeterministicRandom Random { get; }

    string GetBlockId(int localX, int localY, int localZ);

    void SetBlock(int localX, int localY, int localZ, string stableBlockId);

    /// <summary>Returns a stable value independent of invocation order and worker scheduling.</summary>
    ulong Hash(int x, int y, int z, string salt = "");
}

public interface IDeterministicRandom
{
    ulong NextUInt64();

    int NextInt(int exclusiveMax);

    int NextInt(int inclusiveMin, int exclusiveMax);

    double NextDouble();

    IDeterministicRandom Fork(string salt);
}
