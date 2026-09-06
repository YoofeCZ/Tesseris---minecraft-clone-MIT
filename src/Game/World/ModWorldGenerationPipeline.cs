using System.Text;
using OpenTK.Mathematics;
using Tesseris.Game.Blocks;
using Tesseris.Game.Modding;
using Tesseris.ModApi;

namespace Tesseris.Game.World;

/// <summary>
/// Applies the immutable set of mod world-generation hooks to generated chunks.
/// One pipeline can safely be shared by all generation workers.
/// </summary>
public sealed class ModWorldGenerationPipeline
{
    private readonly BlockRegistry _blocks;
    private readonly RegisteredWorldGenerationHook[] _hooks;
    private readonly RegisteredWorldGenerator? _generator;

    public ModWorldGenerationPipeline(
        BlockRegistry blocks,
        IEnumerable<RegisteredWorldGenerationHook> hooks,
        RegisteredWorldGenerator? generator = null)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        ArgumentNullException.ThrowIfNull(hooks);

        _blocks = blocks;
        _generator = generator;
        _hooks = hooks
            .OrderBy(hook => hook.Stage)
            .ThenBy(hook => hook.Priority)
            .ThenBy(hook => hook.Id.Value, StringComparer.Ordinal)
            .ThenBy(hook => hook.ModId, StringComparer.Ordinal)
            .ToArray();

        if (_hooks.Any(hook => hook.Hook is null))
        {
            throw new ArgumentException("A world-generation hook cannot be null.", nameof(hooks));
        }
    }

    public ModWorldGenerationPipeline(BlockRegistry blocks, ModWorldGenerationRegistry hooks)
        : this(blocks, GetHooks(hooks), hooks.Generator)
    {
    }

    public bool HasHooks => _hooks.Length != 0;

    public bool HasBaseGenerator => _generator is not null;

    public bool HasWorldGeneration => HasBaseGenerator || HasHooks;

    /// <summary>Runs the replacement generator against an empty chunk, or returns false for vanilla.</summary>
    public bool GenerateBase(Chunk chunk, Vector3i chunkPosition, long worldSeed)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        if (_generator is null)
        {
            return false;
        }

        ulong generatorSeed = StableHash.GeneratorChunkSeed(worldSeed, chunkPosition, _generator);
        var context = new ChunkGenerationContext(
            chunk,
            _blocks,
            chunkPosition,
            worldSeed,
            generatorSeed);

        try
        {
            _generator.Generator.Generate(context);
            return true;
        }
        catch (Exception exception)
        {
            throw ModWorldGeneratorException.ForChunk(
                _generator.ModId,
                _generator.Id,
                chunkPosition,
                exception);
        }
    }

    /// <summary>Samples the replacement generator's canonical surface, or returns false for vanilla.</summary>
    public bool TrySampleColumn(long worldSeed, int worldX, int worldZ, out ModWorldColumn column)
    {
        if (_generator is null)
        {
            column = default;
            return false;
        }

        ulong generatorSeed = StableHash.GeneratorSeed(worldSeed, _generator);
        var context = new WorldColumnContext(worldSeed, worldX, worldZ, generatorSeed);
        try
        {
            column = _generator.Generator.SampleColumn(context);
            return true;
        }
        catch (Exception exception)
        {
            throw ModWorldGeneratorException.ForColumn(
                _generator.ModId,
                _generator.Id,
                worldX,
                worldZ,
                exception);
        }
    }

    /// <summary>
    /// Runs all hooks in their frozen stage/priority/resource-ID order. Hooks are called sequentially
    /// for one chunk, while separate calls to this method may run concurrently.
    /// </summary>
    public void Apply(Chunk chunk, Vector3i chunkPosition, long worldSeed)
    {
        ArgumentNullException.ThrowIfNull(chunk);

        foreach (RegisteredWorldGenerationHook registration in _hooks)
        {
            ulong hookSeed = StableHash.HookSeed(worldSeed, chunkPosition, registration);
            var context = new ChunkGenerationContext(
                chunk,
                _blocks,
                chunkPosition,
                worldSeed,
                hookSeed);

            try
            {
                registration.Hook.Generate(context);
            }
            catch (Exception exception)
            {
                throw new ModWorldGenerationException(
                    registration.ModId,
                    registration.Id,
                    registration.Stage,
                    chunkPosition,
                    exception);
            }
        }
    }

    private static IReadOnlyList<RegisteredWorldGenerationHook> GetHooks(ModWorldGenerationRegistry hooks)
    {
        ArgumentNullException.ThrowIfNull(hooks);
        return hooks.Hooks;
    }

    private sealed class ChunkGenerationContext : IChunkGenerationContext
    {
        private readonly Chunk _chunk;
        private readonly BlockRegistry _blocks;
        private readonly ulong _hookSeed;

        public ChunkGenerationContext(
            Chunk chunk,
            BlockRegistry blocks,
            Vector3i chunkPosition,
            long worldSeed,
            ulong hookSeed)
        {
            _chunk = chunk;
            _blocks = blocks;
            _hookSeed = hookSeed;
            WorldSeed = worldSeed;
            ChunkX = chunkPosition.X;
            ChunkY = chunkPosition.Y;
            ChunkZ = chunkPosition.Z;
            Random = new DeterministicRandom(hookSeed);
        }

        public long WorldSeed { get; }

        public int ChunkX { get; }

        public int ChunkY { get; }

        public int ChunkZ { get; }

        public int SizeX => Chunk.Size;

        public int SizeY => Chunk.Size;

        public int SizeZ => Chunk.Size;

        public IDeterministicRandom Random { get; }

        public string GetBlockId(int localX, int localY, int localZ)
        {
            ValidateCoordinates(localX, localY, localZ);
            ushort block = _chunk.GetBlock(localX, localY, localZ);

            if (block >= _blocks.Count)
            {
                throw new InvalidOperationException(
                    $"Chunk contains block index {block}, which is not present in the block registry.");
            }

            return _blocks.Definition(block).Id;
        }

        public void SetBlock(int localX, int localY, int localZ, string stableBlockId)
        {
            ValidateCoordinates(localX, localY, localZ);
            ArgumentException.ThrowIfNullOrWhiteSpace(stableBlockId);

            if (!_blocks.TryIndexOf(stableBlockId, out ushort block))
            {
                throw new KeyNotFoundException(
                    $"Block '{stableBlockId}' is not present in the block registry.");
            }

            _chunk.SetBlock(localX, localY, localZ, block);
        }

        public ulong Hash(int x, int y, int z, string salt = "")
        {
            ArgumentNullException.ThrowIfNull(salt);
            return StableHash.Coordinates(_hookSeed, x, y, z, salt);
        }

        private static void ValidateCoordinates(int localX, int localY, int localZ)
        {
            if ((uint)localX >= Chunk.Size)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(localX), localX, $"Local X must be between 0 and {Chunk.Size - 1}.");
            }

            if ((uint)localY >= Chunk.Size)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(localY), localY, $"Local Y must be between 0 and {Chunk.Size - 1}.");
            }

            if ((uint)localZ >= Chunk.Size)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(localZ), localZ, $"Local Z must be between 0 and {Chunk.Size - 1}.");
            }
        }
    }

    private sealed class DeterministicRandom : IDeterministicRandom
    {
        private const ulong Increment = 0x9E3779B97F4A7C15UL;

        private readonly ulong _streamSeed;
        private ulong _state;

        public DeterministicRandom(ulong streamSeed)
        {
            _streamSeed = streamSeed;
            _state = streamSeed;
        }

        public ulong NextUInt64()
        {
            _state += Increment;
            return StableHash.Mix(_state);
        }

        public int NextInt(int exclusiveMax)
        {
            if (exclusiveMax <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(exclusiveMax), "The upper bound must be positive.");
            }

            return (int)NextBounded((ulong)exclusiveMax);
        }

        public int NextInt(int inclusiveMin, int exclusiveMax)
        {
            if (inclusiveMin >= exclusiveMax)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(exclusiveMax), "The upper bound must be greater than the lower bound.");
            }

            ulong range = (ulong)((long)exclusiveMax - inclusiveMin);
            return (int)(inclusiveMin + (long)NextBounded(range));
        }

        public double NextDouble() => (NextUInt64() >> 11) * (1.0 / (1UL << 53));

        public IDeterministicRandom Fork(string salt)
        {
            ArgumentNullException.ThrowIfNull(salt);
            return new DeterministicRandom(StableHash.Text(_streamSeed, salt));
        }

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

    private sealed class WorldColumnContext : IWorldColumnContext
    {
        private readonly ulong _generatorSeed;

        public WorldColumnContext(long worldSeed, int worldX, int worldZ, ulong generatorSeed)
        {
            WorldSeed = worldSeed;
            WorldX = worldX;
            WorldZ = worldZ;
            _generatorSeed = generatorSeed;
        }

        public long WorldSeed { get; }

        public int WorldX { get; }

        public int WorldZ { get; }

        public ulong Hash(int x, int z, string salt = "")
        {
            ArgumentNullException.ThrowIfNull(salt);
            return StableHash.Coordinates(_generatorSeed, x, 0, z, salt);
        }
    }

    private static class StableHash
    {
        private const ulong Offset = 0xCBF29CE484222325UL;
        private const ulong Prime = 0x100000001B3UL;

        public static ulong HookSeed(
            long worldSeed,
            Vector3i chunkPosition,
            RegisteredWorldGenerationHook hook)
        {
            ulong hash = AddUInt64(Offset, unchecked((ulong)worldSeed));
            hash = AddUInt64(hash, unchecked((ulong)(long)chunkPosition.X));
            hash = AddUInt64(hash, unchecked((ulong)(long)chunkPosition.Y));
            hash = AddUInt64(hash, unchecked((ulong)(long)chunkPosition.Z));
            hash = AddUInt64(hash, unchecked((ulong)(int)hook.Stage));
            hash = AddText(hash, hook.ModId);
            hash = AddText(hash, hook.Id.Value);
            return Mix(hash);
        }

        public static ulong GeneratorSeed(long worldSeed, RegisteredWorldGenerator generator)
        {
            ulong hash = AddUInt64(Offset, unchecked((ulong)worldSeed));
            hash = AddText(hash, generator.ModId);
            hash = AddText(hash, generator.Id.Value);
            return Mix(hash);
        }

        public static ulong GeneratorChunkSeed(
            long worldSeed,
            Vector3i chunkPosition,
            RegisteredWorldGenerator generator)
        {
            ulong seed = GeneratorSeed(worldSeed, generator);
            return Coordinates(seed, chunkPosition.X, chunkPosition.Y, chunkPosition.Z, "chunk");
        }

        public static ulong Coordinates(ulong seed, int x, int y, int z, string salt)
        {
            ulong hash = AddUInt64(Offset, seed);
            hash = AddUInt64(hash, unchecked((ulong)(long)x));
            hash = AddUInt64(hash, unchecked((ulong)(long)y));
            hash = AddUInt64(hash, unchecked((ulong)(long)z));
            hash = AddText(hash, salt);
            return Mix(hash);
        }

        public static ulong Text(ulong seed, string value)
        {
            ulong hash = AddUInt64(Offset, seed);
            return Mix(AddText(hash, value));
        }

        public static ulong Mix(ulong value)
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
            Span<byte> bytes = byteCount <= 256
                ? stackalloc byte[byteCount]
                : new byte[byteCount];
            Encoding.UTF8.GetBytes(value, bytes);

            foreach (byte item in bytes)
            {
                hash = (hash ^ item) * Prime;
            }

            return hash;
        }
    }
}

/// <summary>Identifies a replacement world generator that failed while sampling or generating.</summary>
public sealed class ModWorldGeneratorException : Exception
{
    private ModWorldGeneratorException(
        string message,
        string modId,
        ResourceId generatorId,
        Vector3i? chunkPosition,
        int? worldX,
        int? worldZ,
        Exception innerException)
        : base(message, innerException)
    {
        ModId = modId;
        GeneratorId = generatorId;
        ChunkPosition = chunkPosition;
        WorldX = worldX;
        WorldZ = worldZ;
    }

    internal static ModWorldGeneratorException ForChunk(
        string modId,
        ResourceId generatorId,
        Vector3i chunkPosition,
        Exception innerException) =>
        new(
            $"Mod '{modId}' base world generator '{generatorId}' failed for chunk "
            + $"[{chunkPosition.X}, {chunkPosition.Y}, {chunkPosition.Z}].",
            modId,
            generatorId,
            chunkPosition,
            null,
            null,
            innerException);

    internal static ModWorldGeneratorException ForColumn(
        string modId,
        ResourceId generatorId,
        int worldX,
        int worldZ,
        Exception innerException) =>
        new(
            $"Mod '{modId}' base world generator '{generatorId}' failed while sampling column "
            + $"[{worldX}, {worldZ}].",
            modId,
            generatorId,
            null,
            worldX,
            worldZ,
            innerException);

    public string ModId { get; }

    public ResourceId GeneratorId { get; }

    public Vector3i? ChunkPosition { get; }

    public int? WorldX { get; }

    public int? WorldZ { get; }
}

/// <summary>Identifies the mod hook that failed while generating a chunk.</summary>
public sealed class ModWorldGenerationException : Exception
{
    internal ModWorldGenerationException(
        string modId,
        ResourceId hookId,
        WorldGenerationStage stage,
        Vector3i chunkPosition,
        Exception innerException)
        : base(
            $"Mod '{modId}' world-generation hook '{hookId}' failed during stage {stage} "
            + $"for chunk [{chunkPosition.X}, {chunkPosition.Y}, {chunkPosition.Z}].",
            innerException)
    {
        ModId = modId;
        HookId = hookId;
        Stage = stage;
        ChunkPosition = chunkPosition;
    }

    public string ModId { get; }

    public ResourceId HookId { get; }

    public WorldGenerationStage Stage { get; }

    public Vector3i ChunkPosition { get; }
}
