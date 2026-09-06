using Tesseris.Game.Entities;
using Tesseris.ModApi;
using Xunit;

namespace Tesseris.Tests;

public sealed class EntityWorldTests
{
    [Fact]
    public void IDs_are_world_scoped_and_revisions_reject_stale_mutations()
    {
        EntityRegistry registry = EntityTest.RegistryWithValue();
        var firstWorld = new EntityWorld(registry, worldSeed: 5, worldGeneration: 7);
        ModEntityId first = firstWorld.Create(new ResourceId("test:thing"));
        ModEntityId second = firstWorld.Create(new ResourceId("test:thing"));

        Assert.Equal(new ModEntityId(1, 7), first);
        Assert.Equal(new ModEntityId(2, 7), second);
        Assert.True(firstWorld.SetComponent(first, 0, EntityTest.Value("test:value", 12)));
        Assert.False(firstWorld.SetComponent(first, 0, EntityTest.Value("test:value", 99)));
        Assert.True(firstWorld.TryGet(first, out ModEntitySnapshot? snapshot));
        Assert.Equal(1U, snapshot!.Revision);
        Assert.Equal(12, EntityTest.Int(snapshot.Components.Single()));

        var otherWorld = new EntityWorld(registry, worldSeed: 5, worldGeneration: 8);
        Assert.False(otherWorld.TryGet(first, out _));
    }

    [Fact]
    public void Snapshots_and_queries_are_immutable_sorted_copies()
    {
        var registry = new EntityRegistry();
        IModEntityPlatform view = registry.ForMod("test");
        view.RegisterComponent(EntityTest.Component("test:b"));
        view.RegisterComponent(EntityTest.Component("test:a"));
        view.RegisterArchetype(new ModEntityArchetypeDefinition(
            new ResourceId("test:thing"),
            [EntityTest.Value("test:b", 2), EntityTest.Value("test:a", 1)]));
        registry.Freeze();
        var world = new EntityWorld(registry, 1);
        ModEntityId first = world.Create(new ResourceId("test:thing"));
        ModEntityId second = world.Create(new ResourceId("test:thing"));

        IReadOnlyList<ModEntitySnapshot> snapshots = world.WithAll(new ResourceId("test:a"));

        Assert.Equal([first, second], snapshots.Select(value => value.Id));
        Assert.Equal(["test:a", "test:b"], snapshots[0].Components.Select(value => value.ComponentId.Value));
        Assert.Throws<NotSupportedException>(() =>
            ((IList<ModComponentValue>)snapshots[0].Components).Add(EntityTest.Value("test:a", 7)));
        byte[] firstPayload = snapshots[0].Components[0].Value.Payload.ToArray();
        Assert.True(world.SetComponent(first, 0, EntityTest.Value("test:a", 42)));
        Assert.Equal(1, BitConverter.ToInt32(firstPayload));
        Assert.Equal(1, EntityTest.Int(snapshots[0].Components[0]));
    }

    [Fact]
    public void Spatial_index_tracks_create_update_remove_and_required_components()
    {
        ResourceId positionId = new("test:position");
        ResourceId markerId = new("test:marker");
        var registry = new EntityRegistry();
        IModEntityPlatform view = registry.ForMod("test");
        view.RegisterComponent(EntityTest.Component(positionId.Value));
        view.RegisterComponent(EntityTest.Component(markerId.Value));
        view.RegisterArchetype(new ModEntityArchetypeDefinition(
            new ResourceId("test:point"),
            [Position(positionId, 1, 2, 3), EntityTest.Value(markerId.Value, 1)]));
        registry.Freeze();
        var world = new EntityWorld(registry, 1);
        world.ConfigureSpatialIndex(positionId, DecodePosition, cellSize: 8);
        ModEntityId entity = world.Create(new ResourceId("test:point"));

        Assert.Equal(
            [entity],
            world.QuerySpatial(Bounds(0, 0, 0, 4, 4, 4), [markerId]).Select(value => value.Id));
        Assert.True(world.SetComponent(entity, 0, Position(positionId, 30, 2, 3)));
        Assert.Empty(world.QuerySpatial(Bounds(0, 0, 0, 4, 4, 4)));
        Assert.Equal([entity], world.QuerySpatial(Bounds(25, 0, 0, 35, 4, 4)).Select(value => value.Id));
        Assert.True(world.RemoveComponent(entity, 1, positionId));
        Assert.Empty(world.QuerySpatial(Bounds(25, 0, 0, 35, 4, 4)));
    }

    [Fact]
    public void Mutation_from_a_worker_thread_is_rejected()
    {
        EntityRegistry registry = EntityTest.RegistryWithValue();
        var world = new EntityWorld(registry, 1);
        Exception? actual = null;
        var thread = new Thread(() => actual = Record.Exception(() => world.Create(new ResourceId("test:thing"))));
        thread.Start();
        thread.Join();

        InvalidOperationException exception = Assert.IsType<InvalidOperationException>(actual);
        Assert.Contains("simulation thread", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Spatial_queries_reject_unbounded_cell_walks()
    {
        ResourceId positionId = new("test:position");
        var registry = new EntityRegistry();
        IModEntityPlatform view = registry.ForMod("test");
        view.RegisterComponent(EntityTest.Component(positionId.Value));
        view.RegisterArchetype(new ModEntityArchetypeDefinition(new ResourceId("test:point"), []));
        registry.Freeze();
        var world = new EntityWorld(registry, 1);
        world.ConfigureSpatialIndex(positionId, _ => null, cellSize: 1);

        Assert.Throws<ArgumentOutOfRangeException>(() => world.QuerySpatial(
            Bounds(-1_000_000, -1_000_000, -1_000_000, 1_000_000, 1_000_000, 1_000_000)));
    }

    private static ModComponentValue Position(ResourceId id, double x, double y, double z)
    {
        byte[] payload = new byte[sizeof(double) * 3];
        BitConverter.GetBytes(x).CopyTo(payload, 0);
        BitConverter.GetBytes(y).CopyTo(payload, sizeof(double));
        BitConverter.GetBytes(z).CopyTo(payload, sizeof(double) * 2);
        return new ModComponentValue(id, new ModSerializedValue(EntityTest.Serializer, 1, payload));
    }

    private static EntityPosition? DecodePosition(ModSerializedValue value) => new(
        BitConverter.ToDouble(value.Payload.Span[..8]),
        BitConverter.ToDouble(value.Payload.Span.Slice(8, 8)),
        BitConverter.ToDouble(value.Payload.Span.Slice(16, 8)));

    private static EntityBounds Bounds(double x0, double y0, double z0, double x1, double y1, double z1) =>
        new(new EntityPosition(x0, y0, z0), new EntityPosition(x1, y1, z1));
}
