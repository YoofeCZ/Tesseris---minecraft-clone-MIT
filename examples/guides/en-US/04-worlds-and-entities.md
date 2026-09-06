# 4. World presets, dimensions, generation, biomes, and ECS

## Two world-generation extension levels

Use a chunk hook when vanilla terrain remains the base and the mod only adds ores, structures, vegetation, or
post-processing:

```csharp
context.WorldGeneration.Register(
    WorldGenerationStage.Features,
    new ResourceId("author.mymod:ore_veins"),
    priority: 0,
    new OreFeature());
```

Use `SetBaseGenerator` or API-v2 world definitions when the mod owns terrain completely:

```csharp
context.WorldGeneration.SetBaseGenerator(
    new ResourceId("author.mymod:floating_world"),
    generator);
```

The canonical generator is used for chunk data and surface sampling, preventing spawn, streaming, and far
terrain from consulting a different vanilla terrain oracle.

## Determinism and concurrency

Generators and biome sources may execute concurrently for different coordinates. They must use only supplied
context data:

- `WorldSeed`, coordinates, dimension ID;
- `context.Random`, `context.Hash`, or a deterministic fork;
- immutable configuration captured before registries freeze.

Do not use `Random.Shared`, wall-clock time, process `GetHashCode`, thread IDs, mutable shared RNG state, or
loaded neighboring chunks. Cross-chunk structures should derive anchor coordinates from stable hashes and clip
their output into each independently generated chunk.

`IChunkGenerationContext` exposes local chunk coordinates and validates bounds. Block values are stable string
IDs such as `author.mymod:sky_stone`, not runtime numeric registry indices.

## Complete generator

```csharp
internal sealed class FloatingGenerator : IModWorldGenerator
{
    public ModWorldColumn SampleColumn(IWorldColumnContext context)
    {
        int surface = 120 + (int)(context.Hash(context.WorldX, context.WorldZ, "height") % 24);
        return new ModWorldColumn(surface);
    }

    public void Generate(IChunkGenerationContext context)
    {
        for (int x = 0; x < context.SizeX; x++)
        for (int z = 0; z < context.SizeZ; z++)
        for (int y = 0; y < context.SizeY; y++)
        {
            int worldY = context.ChunkY * context.SizeY + y;
            if (worldY <= 120)
            {
                context.SetBlock(x, y, z, "author.mymod:sky_stone");
            }
        }
    }
}
```

For a production implementation with dimension-aware hashes and floating islands, read
[`SkylandsWorld.cs`](../../TotalConversionMod/SkylandsWorld.cs).

## Biomes, dimensions, and presets

A v2 mod obtains the additive context safely:

```csharp
if (context is not IModContextV2 v2)
{
    throw new NotSupportedException("This mod requires Mod API v2.");
}
```

Register providers first, followed by definitions that reference them:

```csharp
v2.WorldDefinitions.RegisterGenerator(Ids.Generator, generator);
v2.WorldDefinitions.RegisterBiomeSource(Ids.BiomeSource, biomeSource);

v2.WorldDefinitions.RegisterBiome(new ModBiomeDefinition(
    Ids.Biome,
    "Azure Skylands",
    Temperature: 0.45f,
    Humidity: 0.30f,
    FeatureIds: Array.Empty<ResourceId>(),
    Properties: Array.Empty<ModComponentValue>()));

v2.WorldDefinitions.RegisterDimension(new ModDimensionDefinition(
    Ids.Dimension,
    "Skylands",
    Ids.Generator,
    Ids.BiomeSource,
    MinimumY: -64,
    Height: 384,
    HasSky: true,
    Properties: Array.Empty<ModComponentValue>()));

v2.WorldDefinitions.RegisterPreset(new ModWorldPresetDefinition(
    Ids.Preset,
    "My Skylands",
    Ids.Dimension,
    new[] { Ids.Dimension },
    Array.Empty<ModComponentValue>()));
```

`IModBiomeSource.Sample` returns a registered biome ID from seed, dimension, and world coordinates. The world
preset appears in the create-world menu. Its selected preset, dimension, and generator fingerprint are stored
with the save.

Each dimension receives an isolated, traversal-safe storage directory. Dimension switching creates a new
immutable world runtime and dispatches lifecycle in a deterministic order.

## ECS components and serializers

Components refer to a registered serializer:

```csharp
v2.Serialization.Register(
    Ids.Int32Serializer,
    currentSchemaVersion: 1,
    new Int32Serializer(),
    Array.Empty<IModDataMigration>());

v2.Entities.RegisterComponent(new ModComponentDescriptor(
    Ids.Energy,
    Ids.Int32Serializer,
    Replicated: true,
    Persisted: true));
```

Definitions registered before serializer freeze carry an explicit `ModSerializedValue` containing serializer
ID, schema version, and owned bytes.

## Archetypes and systems

```csharp
v2.Entities.RegisterArchetype(new ModEntityArchetypeDefinition(
    Ids.Wisp,
    new[] { new ModComponentValue(Ids.Energy, zeroEnergy) }));

v2.Entities.RegisterSystem(
    new ResourceId("author.mymod:wisp_energy_system"),
    ModSystemPhase.Simulation,
    priority: 20,
    new WispEnergySystem());
```

Systems receive a deterministic tick, fixed delta, immutable indexed queries, and a deferred command buffer.
Execution order is phase, priority, then stable system ID. Entity enumeration is ascending stable entity ID.
Creates, destroys, and component changes commit after the phase; stale revisions cannot expose partial state.

Entity IDs include a generation so a destroyed numeric ID cannot become a valid stale reference. Serialized
unknown components remain opaque and survive load/save even when their defining mod is absent.

The fixed simulation clock normally runs at 20 ticks per second, catches up within a bounded limit, and exposes
an interpolation alpha to rendering.

[Next: client APIs](05-client-and-ui.md)
