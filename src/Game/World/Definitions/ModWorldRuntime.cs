using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Tesseris.Game.Blocks;
using Tesseris.Game.Modding;
using Tesseris.ModApi;

namespace Tesseris.Game.World.Definitions;

public enum ModWorldProviderCallback
{
    SampleColumn,
    SampleBiome,
    GenerateChunk,
}

public sealed class ModWorldProviderException : Exception
{
    internal ModWorldProviderException(
        ResourceId dimensionId,
        ResourceId providerId,
        ModWorldProviderCallback callback,
        Exception innerException)
        : base(
            $"World provider '{providerId}' failed during {callback} in dimension '{dimensionId}'.",
            innerException)
    {
        DimensionId = dimensionId;
        ProviderId = providerId;
        Callback = callback;
    }

    public ResourceId DimensionId { get; }
    public ResourceId ProviderId { get; }
    public ModWorldProviderCallback Callback { get; }
}

/// <summary>
/// Immutable, worker-safe selection of one frozen preset and dimension. Switching dimensions creates
/// another runtime sharing the same frozen catalog; lifecycle state lives in <see cref="ModWorldSession"/>.
/// </summary>
public sealed class ModWorldRuntime
{
    private readonly RuntimeCatalog catalog;
    private readonly IModWorldGenerator generator;
    private readonly IModBiomeSource biomeSource;

    private ModWorldRuntime(RuntimeCatalog catalog, ModDimensionDefinition dimension)
    {
        this.catalog = catalog;
        Dimension = dimension;
        generator = catalog.Generators[dimension.GeneratorId];
        biomeSource = catalog.BiomeSources[dimension.BiomeSourceId];
        Fingerprint = ModWorldFingerprint.Compute(catalog, dimension);
    }

    public long WorldSeed => catalog.WorldSeed;
    public ModWorldPresetDefinition Preset => catalog.Preset;
    public ModDimensionDefinition Dimension { get; }
    public IReadOnlyList<ModDimensionDefinition> Dimensions => catalog.Dimensions;
    public string Fingerprint { get; }
    public string StorageKey => ModDimensionStorage.Key(Dimension.Id);

    public static ModWorldRuntime Create(
        ModWorldDefinitionRegistry definitions,
        long worldSeed,
        ResourceId? presetId = null,
        ResourceId? dimensionId = null)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        if (!definitions.IsFrozen)
        {
            throw new InvalidOperationException("World definitions must be frozen before a runtime is created.");
        }

        ModWorldPresetDefinition preset = SelectPreset(definitions, presetId, dimensionId);
        ResourceId selectedId = dimensionId ?? preset.DefaultDimensionId;
        if (!preset.DimensionIds.Contains(selectedId))
        {
            throw new InvalidOperationException(
                $"Dimension '{selectedId}' is not part of preset '{preset.Id}'.");
        }

        var dimensions = new List<ModDimensionDefinition>(preset.DimensionIds.Count);
        var generators = new Dictionary<ResourceId, IModWorldGenerator>();
        var biomeSources = new Dictionary<ResourceId, IModBiomeSource>();
        foreach (ResourceId id in preset.DimensionIds)
        {
            if (!definitions.TryGetDimension(id, out ModDimensionDefinition? dimension) || dimension is null)
            {
                throw new InvalidOperationException($"Preset '{preset.Id}' references missing dimension '{id}'.");
            }

            if (!definitions.TryGetGenerator(dimension.GeneratorId, out IModWorldGenerator? registeredGenerator)
                || registeredGenerator is null)
            {
                throw new InvalidOperationException(
                    $"Dimension '{id}' references missing generator '{dimension.GeneratorId}'.");
            }

            if (!definitions.TryGetBiomeSource(dimension.BiomeSourceId, out IModBiomeSource? registeredSource)
                || registeredSource is null)
            {
                throw new InvalidOperationException(
                    $"Dimension '{id}' references missing biome source '{dimension.BiomeSourceId}'.");
            }

            dimensions.Add(dimension);
            generators[dimension.GeneratorId] = registeredGenerator;
            biomeSources[dimension.BiomeSourceId] = registeredSource;
        }

        ModDimensionDefinition selected = dimensions.Single(value => value.Id == selectedId);
        var catalog = new RuntimeCatalog(
            worldSeed,
            preset,
            Array.AsReadOnly(dimensions.ToArray()),
            generators,
            biomeSources,
            definitions.Biomes.ToDictionary(value => value.Id));
        return new ModWorldRuntime(catalog, selected);
    }

    public ModWorldRuntime SelectDimension(ResourceId dimensionId)
    {
        ModDimensionDefinition? selected = catalog.Dimensions.FirstOrDefault(value => value.Id == dimensionId);
        if (selected is null)
        {
            throw new KeyNotFoundException(
                $"Dimension '{dimensionId}' is not part of preset '{Preset.Id}'.");
        }

        return selected.Id == Dimension.Id ? this : new ModWorldRuntime(catalog, selected);
    }

    public ModWorldColumn SampleColumn(int worldX, int worldZ)
    {
        var context = new RuntimeColumnContext(
            WorldSeed,
            Dimension.Id,
            Dimension.GeneratorId,
            worldX,
            worldZ);
        ModWorldColumn column;
        try
        {
            column = generator.SampleColumn(context);
        }
        catch (Exception exception)
        {
            throw Failure(Dimension.GeneratorId, ModWorldProviderCallback.SampleColumn, exception);
        }

        if (column.SurfaceY < Dimension.MinimumY || column.SurfaceY >= MaximumYExclusive)
        {
            throw new InvalidDataException(
                $"Generator '{Dimension.GeneratorId}' returned surface Y {column.SurfaceY} outside dimension "
                + $"'{Dimension.Id}' range {Dimension.MinimumY}..{MaximumYExclusive - 1}.");
        }

        return column;
    }

    public ModBiomeDefinition SampleBiome(int worldX, int worldY, int worldZ)
    {
        EnsureWorldY(worldY);
        ResourceId biomeId;
        try
        {
            biomeId = biomeSource.Sample(new RuntimeBiomeContext(
                WorldSeed,
                Dimension.Id,
                Dimension.BiomeSourceId,
                worldX,
                worldY,
                worldZ));
        }
        catch (Exception exception)
        {
            throw Failure(Dimension.BiomeSourceId, ModWorldProviderCallback.SampleBiome, exception);
        }

        if (!catalog.Biomes.TryGetValue(biomeId, out ModBiomeDefinition? biome))
        {
            throw new InvalidDataException(
                $"Biome source '{Dimension.BiomeSourceId}' returned unregistered biome '{biomeId}' "
                + $"in dimension '{Dimension.Id}'.");
        }

        return biome;
    }

    public void GenerateChunk(IChunkGenerationContext destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ValidateChunk(destination);
        var context = new RuntimeChunkContext(
            destination,
            WorldSeed,
            Dimension.Id,
            Dimension.GeneratorId);
        try
        {
            generator.Generate(context);
        }
        catch (Exception exception)
        {
            throw Failure(Dimension.GeneratorId, ModWorldProviderCallback.GenerateChunk, exception);
        }
    }

    public void GenerateChunk(
        Chunk chunk,
        BlockRegistry blocks,
        int chunkX,
        int chunkY,
        int chunkZ)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        ArgumentNullException.ThrowIfNull(blocks);
        GenerateChunk(new BlockChunkContext(chunk, blocks, chunkX, chunkY, chunkZ));
    }

    public string StorageDirectory(string worldDirectory) =>
        ModDimensionStorage.Directory(worldDirectory, Dimension.Id);

    public ModWorldSession Open(IModWorldSessionLifecycle? lifecycle = null) => new(this, lifecycle);

    private int MaximumYExclusive => checked(Dimension.MinimumY + Dimension.Height);

    private static ModWorldPresetDefinition SelectPreset(
        ModWorldDefinitionRegistry definitions,
        ResourceId? presetId,
        ResourceId? dimensionId)
    {
        if (presetId is { } requestedPreset)
        {
            if (!definitions.TryGetPreset(requestedPreset, out ModWorldPresetDefinition? selected) || selected is null)
            {
                throw new KeyNotFoundException($"World preset '{requestedPreset}' is not registered.");
            }

            return selected;
        }

        IReadOnlyList<ModWorldPresetDefinition> candidates = dimensionId is { } requestedDimension
            ? definitions.Presets.Where(value => value.DimensionIds.Contains(requestedDimension)).ToArray()
            : definitions.Presets;
        if (candidates.Count == 0)
        {
            throw new InvalidOperationException(dimensionId is null
                ? "No world preset is registered."
                : $"No world preset contains dimension '{dimensionId}'.");
        }

        return candidates[0];
    }

    private void EnsureWorldY(int worldY)
    {
        if (worldY < Dimension.MinimumY || worldY >= MaximumYExclusive)
        {
            throw new ArgumentOutOfRangeException(
                nameof(worldY), worldY,
                $"World Y must be in dimension range {Dimension.MinimumY}..{MaximumYExclusive - 1}.");
        }
    }

    private void ValidateChunk(IChunkGenerationContext chunk)
    {
        if (chunk.SizeX <= 0 || chunk.SizeY <= 0 || chunk.SizeZ <= 0)
        {
            throw new ArgumentException("Chunk dimensions must be positive.", nameof(chunk));
        }

        int minimum;
        int maximum;
        try
        {
            minimum = checked(chunk.ChunkY * chunk.SizeY);
            maximum = checked(minimum + chunk.SizeY);
        }
        catch (OverflowException)
        {
            throw new ArgumentOutOfRangeException(nameof(chunk), "Chunk vertical coordinates overflow.");
        }

        if (minimum < Dimension.MinimumY || maximum > MaximumYExclusive)
        {
            throw new ArgumentOutOfRangeException(
                nameof(chunk),
                $"Chunk Y range {minimum}..{maximum - 1} is outside dimension '{Dimension.Id}' range "
                + $"{Dimension.MinimumY}..{MaximumYExclusive - 1}.");
        }
    }

    private ModWorldProviderException Failure(
        ResourceId providerId,
        ModWorldProviderCallback callback,
        Exception exception) =>
        new(Dimension.Id, providerId, callback, exception);

    private sealed class BlockChunkContext : IChunkGenerationContext, IChunkBackedGenerationContext
    {
        private readonly BlockRegistry blocks;

        public BlockChunkContext(Chunk chunk, BlockRegistry blocks, int chunkX, int chunkY, int chunkZ)
        {
            Chunk = chunk;
            this.blocks = blocks;
            ChunkX = chunkX;
            ChunkY = chunkY;
            ChunkZ = chunkZ;
            Random = new ModWorldDeterministicRandom(0);
        }

        public long WorldSeed => 0;
        public int ChunkX { get; }
        public int ChunkY { get; }
        public int ChunkZ { get; }
        public int SizeX => Chunk.Size;
        public int SizeY => Chunk.Size;
        public int SizeZ => Chunk.Size;
        public IDeterministicRandom Random { get; }
        public Chunk Chunk { get; }

        public string GetBlockId(int localX, int localY, int localZ)
        {
            ushort block = Chunk.GetBlock(localX, localY, localZ);
            if (block >= blocks.Count)
            {
                throw new InvalidDataException($"Chunk block index {block} is not registered.");
            }

            return blocks.Definition(block).Id;
        }

        public void SetBlock(int localX, int localY, int localZ, string stableBlockId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(stableBlockId);
            if (!blocks.TryIndexOf(stableBlockId, out ushort block))
            {
                throw new KeyNotFoundException($"Block '{stableBlockId}' is not registered.");
            }

            Chunk.SetBlock(localX, localY, localZ, block);
        }

        public ulong Hash(int x, int y, int z, string salt = "") => 0;
    }

    internal sealed record RuntimeCatalog(
        long WorldSeed,
        ModWorldPresetDefinition Preset,
        IReadOnlyList<ModDimensionDefinition> Dimensions,
        IReadOnlyDictionary<ResourceId, IModWorldGenerator> Generators,
        IReadOnlyDictionary<ResourceId, IModBiomeSource> BiomeSources,
        IReadOnlyDictionary<ResourceId, ModBiomeDefinition> Biomes);
}

internal static class ModWorldFingerprint
{
    public static string Compute(ModWorldRuntime.RuntimeCatalog catalog, ModDimensionDefinition selected)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        // Persisted v1 fingerprint domain; product rename must not invalidate existing worlds.
        Add(hash, "voxelity-world-runtime-v1");
        AddPreset(hash, catalog.Preset);
        Add(hash, selected.Id.Value);
        foreach (ModDimensionDefinition dimension in catalog.Dimensions.OrderBy(value => value.Id.Value, StringComparer.Ordinal))
        {
            AddDimension(hash, dimension);
            Add(hash, catalog.Generators[dimension.GeneratorId].GetType().FullName ?? string.Empty);
            Add(hash, catalog.BiomeSources[dimension.BiomeSourceId].GetType().FullName ?? string.Empty);
        }

        foreach (ModBiomeDefinition biome in catalog.Biomes.Values.OrderBy(value => value.Id.Value, StringComparer.Ordinal))
        {
            AddBiome(hash, biome);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void AddPreset(IncrementalHash hash, ModWorldPresetDefinition value)
    {
        Add(hash, value.Id.Value);
        Add(hash, value.DisplayName);
        Add(hash, value.DefaultDimensionId.Value);
        foreach (ResourceId id in value.DimensionIds) Add(hash, id.Value);
        AddProperties(hash, value.Properties);
    }

    private static void AddDimension(IncrementalHash hash, ModDimensionDefinition value)
    {
        Add(hash, value.Id.Value);
        Add(hash, value.DisplayName);
        Add(hash, value.GeneratorId.Value);
        Add(hash, value.BiomeSourceId.Value);
        Add(hash, value.MinimumY);
        Add(hash, value.Height);
        Add(hash, value.HasSky ? 1 : 0);
        AddProperties(hash, value.Properties);
    }

    private static void AddBiome(IncrementalHash hash, ModBiomeDefinition value)
    {
        Add(hash, value.Id.Value);
        Add(hash, value.DisplayName);
        Add(hash, BitConverter.SingleToInt32Bits(value.Temperature));
        Add(hash, BitConverter.SingleToInt32Bits(value.Humidity));
        foreach (ResourceId id in value.FeatureIds) Add(hash, id.Value);
        AddProperties(hash, value.Properties);
    }

    private static void AddProperties(IncrementalHash hash, IReadOnlyList<ModComponentValue> values)
    {
        Add(hash, values.Count);
        foreach (ModComponentValue value in values.OrderBy(item => item.ComponentId.Value, StringComparer.Ordinal))
        {
            Add(hash, value.ComponentId.Value);
            Add(hash, value.Value.SerializerId.Value);
            Add(hash, value.Value.SchemaVersion);
            Add(hash, value.Value.Payload.Span);
        }
    }

    private static void Add(IncrementalHash hash, string value) => Add(hash, Encoding.UTF8.GetBytes(value));

    private static void Add(IncrementalHash hash, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static void Add(IncrementalHash hash, ReadOnlySpan<byte> value)
    {
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(length, value.Length);
        hash.AppendData(length);
        hash.AppendData(value);
    }
}
