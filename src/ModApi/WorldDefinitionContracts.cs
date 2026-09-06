namespace Tesseris.ModApi;

public sealed record ModWorldPresetDefinition(
    ResourceId Id,
    string DisplayName,
    ResourceId DefaultDimensionId,
    IReadOnlyList<ResourceId> DimensionIds,
    IReadOnlyList<ModComponentValue> Properties);

public sealed record ModDimensionDefinition(
    ResourceId Id,
    string DisplayName,
    ResourceId GeneratorId,
    ResourceId BiomeSourceId,
    int MinimumY,
    int Height,
    bool HasSky,
    IReadOnlyList<ModComponentValue> Properties);

public sealed record ModBiomeDefinition(
    ResourceId Id,
    string DisplayName,
    float Temperature,
    float Humidity,
    IReadOnlyList<ResourceId> FeatureIds,
    IReadOnlyList<ModComponentValue> Properties);

public interface IModBiomeSampleContext
{
    long WorldSeed { get; }

    ResourceId DimensionId { get; }

    int WorldX { get; }

    int WorldY { get; }

    int WorldZ { get; }

    ulong Hash(string salt = "");
}

/// <summary>Thread-safe deterministic biome selector used before chunks exist.</summary>
public interface IModBiomeSource
{
    ResourceId Sample(IModBiomeSampleContext context);
}

/// <summary>
/// Frozen world-definition registry. All IDs supplied by a mod must use its namespace. Definitions are
/// immutable snapshots; duplicate IDs fail configuration. Generators and biome sources may run concurrently
/// and must depend only on their deterministic contexts.
/// </summary>
public interface IModWorldDefinitionRegistry
{
    void RegisterPreset(ModWorldPresetDefinition definition);

    void RegisterDimension(ModDimensionDefinition definition);

    void RegisterBiome(ModBiomeDefinition definition);

    void RegisterGenerator(ResourceId id, IModWorldGenerator generator);

    void RegisterBiomeSource(ResourceId id, IModBiomeSource source);
}
