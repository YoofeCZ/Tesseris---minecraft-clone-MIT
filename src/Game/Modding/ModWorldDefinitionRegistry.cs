using Tesseris.ModApi;

namespace Tesseris.Game.Modding;

/// <summary>
/// Owns immutable world presets, dimensions, biomes and their executable providers. Registration is
/// mod-scoped and is frozen before any world worker starts.
/// </summary>
public sealed class ModWorldDefinitionRegistry
{
    private readonly int mainThreadId = Environment.CurrentManagedThreadId;
    private readonly Dictionary<ResourceId, Owned<ModWorldPresetDefinition>> presets = [];
    private readonly Dictionary<ResourceId, Owned<ModDimensionDefinition>> dimensions = [];
    private readonly Dictionary<ResourceId, Owned<ModBiomeDefinition>> biomes = [];
    private readonly Dictionary<ResourceId, Owned<IModWorldGenerator>> generators = [];
    private readonly Dictionary<ResourceId, Owned<IModBiomeSource>> biomeSources = [];

    public bool IsFrozen { get; private set; }

    public IReadOnlyList<ModWorldPresetDefinition> Presets => OrderedValues(presets);

    public IReadOnlyList<ModDimensionDefinition> Dimensions => OrderedValues(dimensions);

    public IReadOnlyList<ModBiomeDefinition> Biomes => OrderedValues(biomes);

    internal IModWorldDefinitionRegistry ForMod(string modId)
    {
        EnsureMainThread();
        ArgumentException.ThrowIfNullOrWhiteSpace(modId);
        return new View(this, modId.Trim().ToLowerInvariant());
    }

    internal void Freeze()
    {
        EnsureMainThread();
        if (IsFrozen)
        {
            return;
        }

        foreach (Owned<ModWorldPresetDefinition> owned in presets.Values)
        {
            ModWorldPresetDefinition preset = owned.Value;
            if (!preset.DimensionIds.Contains(preset.DefaultDimensionId))
            {
                throw Invalid(owned.Owner, preset.Id,
                    $"default dimension '{preset.DefaultDimensionId}' is not listed by the preset");
            }

            foreach (ResourceId dimensionId in preset.DimensionIds)
            {
                if (!dimensions.ContainsKey(dimensionId))
                {
                    throw Invalid(owned.Owner, preset.Id,
                        $"references missing dimension '{dimensionId}'");
                }
            }
        }

        foreach (Owned<ModDimensionDefinition> owned in dimensions.Values)
        {
            ModDimensionDefinition dimension = owned.Value;
            if (!generators.ContainsKey(dimension.GeneratorId))
            {
                throw Invalid(owned.Owner, dimension.Id,
                    $"references missing generator '{dimension.GeneratorId}'");
            }

            if (!biomeSources.ContainsKey(dimension.BiomeSourceId))
            {
                throw Invalid(owned.Owner, dimension.Id,
                    $"references missing biome source '{dimension.BiomeSourceId}'");
            }
        }

        IsFrozen = true;
    }

    public bool TryGetPreset(ResourceId id, out ModWorldPresetDefinition? definition) =>
        TryValue(presets, id, out definition);

    public bool TryGetDimension(ResourceId id, out ModDimensionDefinition? definition) =>
        TryValue(dimensions, id, out definition);

    public bool TryGetBiome(ResourceId id, out ModBiomeDefinition? definition) =>
        TryValue(biomes, id, out definition);

    public bool TryGetGenerator(ResourceId id, out IModWorldGenerator? generator) =>
        TryValue(generators, id, out generator);

    public bool TryGetBiomeSource(ResourceId id, out IModBiomeSource? source) =>
        TryValue(biomeSources, id, out source);

    private void RegisterPreset(string owner, ModWorldPresetDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ValidateDefinition(owner, definition.Id, definition.DisplayName);
        ResourceId[] dimensionsSnapshot = SnapshotDistinct(definition.DimensionIds, owner, definition.Id, "dimension");
        if (dimensionsSnapshot.Length == 0)
        {
            throw Invalid(owner, definition.Id, "must contain at least one dimension");
        }

        Add(presets, owner, definition.Id, definition with
        {
            DimensionIds = Array.AsReadOnly(dimensionsSnapshot),
            Properties = SnapshotComponents(definition.Properties),
        });
    }

    private void RegisterDimension(string owner, ModDimensionDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ValidateDefinition(owner, definition.Id, definition.DisplayName);
        if (definition.Height <= 0)
        {
            throw Invalid(owner, definition.Id, "height must be positive");
        }

        try
        {
            _ = checked(definition.MinimumY + definition.Height);
        }
        catch (OverflowException exception)
        {
            throw new ModHostException(
                $"Mod '{owner}' dimension '{definition.Id}' vertical range overflows Int32.", exception);
        }

        Add(dimensions, owner, definition.Id, definition with
        {
            Properties = SnapshotComponents(definition.Properties),
        });
    }

    private void RegisterBiome(string owner, ModBiomeDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ValidateDefinition(owner, definition.Id, definition.DisplayName);
        if (!float.IsFinite(definition.Temperature) || !float.IsFinite(definition.Humidity))
        {
            throw Invalid(owner, definition.Id, "temperature and humidity must be finite");
        }

        Add(biomes, owner, definition.Id, definition with
        {
            FeatureIds = Array.AsReadOnly(SnapshotDistinct(definition.FeatureIds, owner, definition.Id, "feature")),
            Properties = SnapshotComponents(definition.Properties),
        });
    }

    private void RegisterGenerator(string owner, ResourceId id, IModWorldGenerator generator)
    {
        ArgumentNullException.ThrowIfNull(generator);
        ValidateOwnedId(owner, id);
        Add(generators, owner, id, generator);
    }

    private void RegisterBiomeSource(string owner, ResourceId id, IModBiomeSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        ValidateOwnedId(owner, id);
        Add(biomeSources, owner, id, source);
    }

    private void Add<T>(Dictionary<ResourceId, Owned<T>> target, string owner, ResourceId id, T value)
    {
        EnsureMainThread();
        EnsureMutable();
        ValidateOwnedId(owner, id);
        if (!target.TryAdd(id, new Owned<T>(owner, value)))
        {
            throw new ModHostException($"World definition '{id}' is already registered by '{target[id].Owner}'.");
        }
    }

    private static void ValidateDefinition(string owner, ResourceId id, string displayName)
    {
        ValidateOwnedId(owner, id);
        if (string.IsNullOrWhiteSpace(displayName))
        {
            throw Invalid(owner, id, "display name must not be empty");
        }
    }

    private static void ValidateOwnedId(string owner, ResourceId id)
    {
        if (!id.Value.StartsWith(owner + ":", StringComparison.Ordinal))
        {
            throw new ModHostException($"Mod '{owner}' may only register world definitions in its own namespace.");
        }
    }

    private static ResourceId[] SnapshotDistinct(
        IReadOnlyList<ResourceId> values,
        string owner,
        ResourceId id,
        string kind)
    {
        ArgumentNullException.ThrowIfNull(values);
        ResourceId[] snapshot = values.ToArray();
        if (snapshot.Distinct().Count() != snapshot.Length)
        {
            throw Invalid(owner, id, $"contains duplicate {kind} IDs");
        }

        return snapshot;
    }

    private static IReadOnlyList<ModComponentValue> SnapshotComponents(IReadOnlyList<ModComponentValue> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return Array.AsReadOnly(values.Select(value =>
            new ModComponentValue(
                value.ComponentId,
                value.Value with { Payload = value.Value.Payload.ToArray() })).ToArray());
    }

    private static IReadOnlyList<T> OrderedValues<T>(Dictionary<ResourceId, Owned<T>> source) =>
        Array.AsReadOnly(source
            .OrderBy(pair => pair.Key.Value, StringComparer.Ordinal)
            .Select(pair => pair.Value.Value)
            .ToArray());

    private static bool TryValue<T>(Dictionary<ResourceId, Owned<T>> source, ResourceId id, out T? value)
    {
        if (id.Value.StartsWith("voxelity:", StringComparison.Ordinal))
        {
            id = new ResourceId("tesseris:" + id.Value["voxelity:".Length..]);
        }

        if (source.TryGetValue(id, out Owned<T>? owned))
        {
            value = owned.Value;
            return true;
        }

        value = default;
        return false;
    }

    private static ModHostException Invalid(string owner, ResourceId id, string reason) =>
        new($"Mod '{owner}' world definition '{id}' {reason}.");

    private void EnsureMutable()
    {
        if (IsFrozen)
        {
            throw new InvalidOperationException("World definition registration is frozen.");
        }
    }

    private void EnsureMainThread()
    {
        if (Environment.CurrentManagedThreadId != mainThreadId)
        {
            throw new InvalidOperationException("World definitions may only be changed on the game thread.");
        }
    }

    private sealed record Owned<T>(string Owner, T Value);

    private sealed class View(ModWorldDefinitionRegistry registry, string owner) : IModWorldDefinitionRegistry
    {
        public void RegisterPreset(ModWorldPresetDefinition definition) => registry.RegisterPreset(owner, definition);

        public void RegisterDimension(ModDimensionDefinition definition) => registry.RegisterDimension(owner, definition);

        public void RegisterBiome(ModBiomeDefinition definition) => registry.RegisterBiome(owner, definition);

        public void RegisterGenerator(ResourceId id, IModWorldGenerator generator) =>
            registry.RegisterGenerator(owner, id, generator);

        public void RegisterBiomeSource(ResourceId id, IModBiomeSource source) =>
            registry.RegisterBiomeSource(owner, id, source);
    }
}
