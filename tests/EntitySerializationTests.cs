using Tesseris.Game.Entities;
using Tesseris.ModApi;
using Xunit;

namespace Tesseris.Tests;

public sealed class EntitySerializationTests
{
    [Fact]
    public void Save_is_deterministic_and_round_trip_preserves_ids_revisions_and_next_id()
    {
        EntityRegistry registry = EntityTest.RegistryWithValue();
        var original = new EntityWorld(registry, worldSeed: 77, worldGeneration: 9);
        ModEntityId first = original.Create(new ResourceId("test:thing"));
        _ = original.Create(new ResourceId("test:thing"));
        Assert.True(original.SetComponent(first, 0, EntityTest.Value("test:value", 123, schemaVersion: 8)));

        byte[] firstSave = original.SaveToBytes();
        byte[] secondSave = original.SaveToBytes();
        using var stream = new MemoryStream(firstSave);
        EntityWorld loaded = EntityWorld.Load(registry, stream);

        Assert.Equal(firstSave, secondSave);
        Assert.Equal(firstSave, loaded.SaveToBytes());
        Assert.True(loaded.TryGet(first, out ModEntitySnapshot? snapshot));
        Assert.Equal(123, EntityTest.Int(snapshot!.Components.Single()));
        Assert.Equal(8, snapshot.Components.Single().Value.SchemaVersion);
        Assert.Equal(new ModEntityId(3, 9), loaded.Create(new ResourceId("test:thing")));
    }

    [Fact]
    public void Unknown_component_and_archetype_blobs_survive_without_the_owning_mod()
    {
        var sourceRegistry = new EntityRegistry();
        IModEntityPlatform sourceView = sourceRegistry.ForMod("absent");
        var serializer = new ResourceId("absent:future_serializer");
        sourceView.RegisterComponent(new ModComponentDescriptor(new ResourceId("absent:future"), serializer));
        byte[] payload = [0, 1, 2, 253, 254, 255];
        sourceView.RegisterArchetype(new ModEntityArchetypeDefinition(
            new ResourceId("absent:entity"),
            [new ModComponentValue(
                new ResourceId("absent:future"),
                new ModSerializedValue(serializer, 999, payload))]));
        sourceRegistry.Freeze();
        var source = new EntityWorld(sourceRegistry, 42, 3);
        ModEntityId id = source.Create(new ResourceId("absent:entity"));
        byte[] bytes = source.SaveToBytes();

        var emptyRegistry = new EntityRegistry();
        emptyRegistry.Freeze();
        using var stream = new MemoryStream(bytes);
        EntityWorld loaded = EntityWorld.Load(emptyRegistry, stream);

        Assert.True(loaded.TryGet(id, out ModEntitySnapshot? snapshot));
        Assert.Equal(new ResourceId("absent:entity"), snapshot!.ArchetypeId);
        ModComponentValue component = Assert.Single(snapshot.Components);
        Assert.Equal(new ResourceId("absent:future"), component.ComponentId);
        Assert.Equal(serializer, component.Value.SerializerId);
        Assert.Equal(999, component.Value.SchemaVersion);
        Assert.Equal(payload, component.Value.Payload.ToArray());
        Assert.Equal(bytes, loaded.SaveToBytes());
    }

    [Fact]
    public void Corrupt_or_truncated_files_are_rejected_without_partial_world()
    {
        EntityRegistry registry = EntityTest.RegistryWithValue();
        var world = new EntityWorld(registry, 1);
        world.Create(new ResourceId("test:thing"));
        byte[] valid = world.SaveToBytes();
        byte[] truncated = valid[..^2];
        byte[] badMagic = valid.ToArray();
        badMagic[0] ^= 0x7F;

        Assert.Throws<InvalidDataException>(() =>
            EntityWorld.Load(registry, new MemoryStream(truncated)));
        Assert.Throws<InvalidDataException>(() =>
            EntityWorld.Load(registry, new MemoryStream(badMagic)));
    }

    [Fact]
    public void Serialization_must_run_on_the_simulation_thread()
    {
        EntityRegistry registry = EntityTest.RegistryWithValue();
        var world = new EntityWorld(registry, 1);
        Exception? actual = null;
        var thread = new Thread(() => actual = Record.Exception(() => world.SaveToBytes()));
        thread.Start();
        thread.Join();

        Assert.IsType<InvalidOperationException>(actual);
    }
}
